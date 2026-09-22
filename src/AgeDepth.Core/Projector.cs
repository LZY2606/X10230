using System.Text.Json;
using System.Text.Json.Nodes;

namespace AgeDepth.Core;

/// <summary>Derived state folded from the event log; materials stay immutable, decisions layer on top.</summary>
public sealed class Projection
{
    public List<(string id, JsonObject payload)> Segments { get; } = new();
    public List<(string id, JsonObject payload)> Anchors { get; } = new();
    public List<(string id, JsonObject payload)> EventLayers { get; } = new();
    public List<JsonObject> Decisions { get; } = new();
    public HashSet<string> ExcludedAnchors { get; } = new(StringComparer.Ordinal);
    public EngineParams Params { get; set; } = EngineParams.Default;
    public List<Diagnostic> Diagnostics { get; } = new();
}

public static class Projector
{
    public static Projection Project(EventStore store, long? upToSeq = null, string? branchKeepAnchor = null)
    {
        var p = new Projection();
        foreach (var m in store.Materials())
        {
            var payload = (JsonObject)JsonNode.Parse(m.payload)!;
            switch (m.kind)
            {
                case "segment": p.Segments.Add((m.id, payload)); break;
                case "anchor": p.Anchors.Add((m.id, payload)); break;
                case "event-layer": p.EventLayers.Add((m.id, payload)); break;
                case "exclusion":
                    if (payload["anchor"]?.GetValue<string>() is { } a) p.ExcludedAnchors.Add(a);
                    break;
            }
        }
        foreach (var e in store.Events(upToSeq))
        {
            var payload = (JsonObject)JsonNode.Parse(e.Payload)!;
            switch (e.Type)
            {
                case "params.revised":
                    p.Params = new EngineParams(
                        payload["version"]!.GetValue<string>(),
                        payload["draws"]!.GetValue<int>(),
                        payload["maxAnchors"]!.GetValue<int>(),
                        payload["maxCandidates"]!.GetValue<int>());
                    break;
                case "decision.made":
                    p.Decisions.Add(payload);
                    var kind = payload["kind"]!.GetValue<string>();
                    var target = payload["target"]!.GetValue<string>();
                    if (kind == "exclude-anchor") p.ExcludedAnchors.Add(target);
                    if (kind == "keep-anchor" && (branchKeepAnchor is null || branchKeepAnchor == target))
                        p.ExcludedAnchors.Remove(target);
                    break;
            }
        }
        return p;
    }

    /// <summary>Builds the engine input; unconvertible values stay raw in materials and surface as diagnostics.</summary>
    public static ModelInput BuildInput(Projection p)
    {
        var segments = p.Segments.Select(s =>
        {
            var pl = s.payload;
            return new SegmentInput(
                pl["id"]!.GetValue<string>(),
                pl["top"]!["value"]!.GetValue<double>(),
                pl["base"]!["value"]!.GetValue<double>(),
                pl["top"]!["unit"]!.GetValue<string>(),
                pl["datum"]!.GetValue<string>(), pl["boundary"]!.GetValue<string>());
        }).ToList();
        var anchors = p.Anchors.Select(a =>
        {
            var pl = a.payload;
            var depth = pl["depth"]!;
            var age = pl["age"]!;
            var samples = age["samples"] as JsonArray;
            return new AnchorInput(
                pl["id"]!.GetValue<string>(),
                depth["value"]!.GetValue<double>(), depth["unit"]!.GetValue<string>(),
                depth["datum"]!.GetValue<string>(),
                samples?.Select(x => x!.GetValue<double>()).ToArray() ?? Array.Empty<double>(),
                age["unit"]!.GetValue<string>(),
                pl["reservoirOffset"]?["value"]?.GetValue<double>(),
                pl["reservoirOffset"]?["unit"]?.GetValue<string>(),
                depth["raw"]?.GetValue<string>() ?? "");
        }).ToList();
        var layers = p.EventLayers.Select(l =>
        {
            var pl = l.payload;
            var d = pl["depth"]!;
            return new EventLayerInput(pl["id"]!.GetValue<string>(),
                d["value"]!.GetValue<double>(), d["unit"]!.GetValue<string>(),
                d["datum"]!.GetValue<string>(),
                pl["name"]!.GetValue<string>(), pl["correlatesTo"]?.GetValue<string>() ?? "");
        }).ToList();
        return new ModelInput(segments, anchors, layers, p.ExcludedAnchors.ToList());
    }
}

/// <summary>Ties store + engine together; every run records input digest, param version and parent run.</summary>
public sealed class Workspace
{
    public EventStore Store { get; }
    public Workspace(EventStore store) => Store = store;

    /// <summary>
    /// Recomputes only the segments touched by <paramref name="changedMaterials"/> (local recompute);
    /// unaffected segment results are carried over from the parent run with their provenance.
    /// </summary>
    public (StoredRun run, ComputeResult result) ComputeAndStore(
        string reason, IReadOnlyList<string>? changedMaterials = null, string? branchKeepAnchor = null)
    {
        var projection = Projector.Project(Store, branchKeepAnchor: branchKeepAnchor);
        var input = Projector.BuildInput(projection);
        var parent = Store.LatestRun();

        var affected = AffectedSegments(input, changedMaterials);
        var compute = Engine.Compute(input, projection.Params);

        JsonNode resultJson;
        string status;
        if (compute.Output is null)
        {
            status = compute.Failure.ToString();
            resultJson = JsonNode.Parse(JsonSerializer.Serialize(new
            {
                failure = compute.Failure.ToString(),
                diagnostics = compute.Diagnostics,
                reason
            }))!;
        }
        else
        {
            status = compute.Failure == FailureKind.None ? "ok" : compute.Failure.ToString();
            var carried = new List<SegmentModel>();
            if (parent is not null && changedMaterials is not null)
            {
                var parentOutput = JsonSerializer.Deserialize<ModelOutput>(Store.GetRun(parent.Id)!.ResultJson,
                    new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
                if (parentOutput is not null)
                    carried = parentOutput.Segments.Where(s => !affected.Contains(s.SegmentId)).ToList();
            }
            var merged = compute.Output.Segments
                .Where(s => affected.Contains(s.SegmentId) || carried.All(c => c.SegmentId != s.SegmentId))
                .Concat(carried)
                .OrderBy(s => s.SegmentId, StringComparer.Ordinal).ToList();
            var output = compute.Output with { Segments = merged };
            resultJson = JsonNode.Parse(JsonSerializer.Serialize(output))!;
        }

        var runId = "run-" + Canon.Digest(Canon.Serialize(resultJson) + "|" + (parent?.Id ?? "root"))[..12];
        var ev = Store.Append("model.computed", new JsonObject
        {
            ["runId"] = runId,
            ["reason"] = reason,
            ["affected"] = new JsonArray(affected.Select(a => (JsonNode)JsonValue.Create(a)).ToArray()),
            ["changedMaterials"] = new JsonArray((changedMaterials ?? Array.Empty<string>()).Select(a => (JsonNode)JsonValue.Create(a)).ToArray())
        }, parent is null ? null : new[] { parent.Id }, projection.Params.Version,
            compute.Output?.InputDigest);
        var run = new StoredRun(runId, ev.Seq, compute.Output?.InputDigest ?? "", projection.Params.Version,
            parent?.Id ?? "", Canon.Serialize(new JsonArray(affected.Select(a => (JsonNode)JsonValue.Create(a)).ToArray())),
            status, Canon.Serialize(resultJson));
        Store.SaveRun(run);
        return (run, compute);
    }

    private static List<string> AffectedSegments(ModelInput input, IReadOnlyList<string>? changedMaterials)
    {
        if (changedMaterials is null || changedMaterials.Count == 0)
            return input.Segments.Select(s => s.Id).ToList();
        var changed = new HashSet<string>(changedMaterials, StringComparer.Ordinal);
        var affected = new HashSet<string>(StringComparer.Ordinal);
        foreach (var a in input.Anchors.Where(a => changed.Contains(a.Id)))
        {
            var f = Units.DepthToMetres(a.DepthUnit);
            if (f is null) continue;
            var d = a.Depth * f.Value;
            foreach (var s in input.Segments)
            {
                var sf = Units.DepthToMetres(s.Unit);
                if (sf is null) continue;
                if (d >= s.Top * sf.Value - 1e-9 && d <= s.Base * sf.Value + 1e-9) affected.Add(s.Id);
            }
        }
        foreach (var s in input.Segments.Where(s => changed.Contains(s.Id))) affected.Add(s.Id);
        return affected.Count == 0 ? input.Segments.Select(s => s.Id).ToList() : affected.ToList();
    }
}
