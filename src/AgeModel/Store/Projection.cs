using System.Text.Json;
using System.Text.Json.Nodes;
using AgeModel.Domain;

namespace AgeModel.Store;

public static class MaterialJson
{
    public static JsonNode ToJson(Material m)
    {
        var o = new JsonObject { ["kind"] = m.Kind, ["id"] = m.Id, ["importSeq"] = m.ImportSeq };
        switch (m)
        {
            case DepthDatumMaterial d:
                o["datumName"] = d.DatumName; o["referenceFrame"] = d.ReferenceFrame; break;
            case BoundaryMaterial b:
                o["boundaryKind"] = b.BoundaryKind; o["depth"] = QuantityJson(b.Depth); o["reason"] = b.Reason; break;
            case RadiometricAgeMaterial r:
                o["depth"] = QuantityJson(r.Depth);
                o["age"] = new JsonObject
                {
                    ["unit"] = r.Age.Unit,
                    ["quantiles"] = new JsonArray(r.Age.Quantiles.Select(q =>
                        (JsonNode)new JsonObject { ["p"] = q.P, ["age"] = q.Age }).ToArray())
                };
                o["labCode"] = r.LabCode;
                if (r.ReservoirOffsetYears is { } ro) o["reservoirOffsetYears"] = QuantityJson(ro);
                break;
            case EventLayerMaterial e:
                o["depth"] = QuantityJson(e.Depth); o["name"] = e.Name; o["correlatedTo"] = e.CorrelatedTo; break;
            case ExclusionDecisionMaterial x:
                o["targetMaterialId"] = x.TargetMaterialId; o["reason"] = x.Reason; o["decidedBy"] = x.DecidedBy; break;
        }
        return o;
    }

    public static JsonObject QuantityJson(Quantity q)
    {
        var o = new JsonObject { ["raw"] = q.Raw, ["unit"] = q.Unit, ["datum"] = q.Datum };
        if (q.Value is { } v) o["value"] = v;
        if (q.Diagnostic is { } d) o["diagnostic"] = d;
        return o;
    }

    public static Quantity QuantityFrom(JsonNode? n)
        => Quantity.Parse(n?["raw"]?.GetValue<string>() ?? "", n?["unit"]?.GetValue<string>() ?? "", n?["datum"]?.GetValue<string>() ?? "");

    public static Material FromJson(JsonNode n)
    {
        var kind = n["kind"]!.GetValue<string>();
        var id = n["id"]!.GetValue<string>();
        var seq = n["importSeq"]!.GetValue<int>();
        return kind switch
        {
            MaterialKind.DepthDatum => new DepthDatumMaterial(id, seq,
                n["datumName"]!.GetValue<string>(), n["referenceFrame"]!.GetValue<string>()),
            MaterialKind.SegmentGap or MaterialKind.Unconformity => new BoundaryMaterial(id, seq, kind,
                QuantityFrom(n["depth"]), n["reason"]!.GetValue<string>()),
            MaterialKind.RadiometricAge => new RadiometricAgeMaterial(id, seq,
                QuantityFrom(n["depth"]),
                new AgeDistribution(n["age"]!["quantiles"]!.AsArray()
                    .Select(q => new QuantilePoint(q!["p"]!.GetValue<double>(), q["age"]!.GetValue<double>())).ToList(),
                    n["age"]!["unit"]!.GetValue<string>()),
                n["labCode"]!.GetValue<string>(),
                n["reservoirOffsetYears"] is { } ro ? QuantityFrom(ro) : null),
            MaterialKind.EventLayer => new EventLayerMaterial(id, seq,
                QuantityFrom(n["depth"]), n["name"]!.GetValue<string>(), n["correlatedTo"]!.GetValue<string>()),
            MaterialKind.ExclusionDecision => new ExclusionDecisionMaterial(id, seq,
                n["targetMaterialId"]!.GetValue<string>(), n["reason"]!.GetValue<string>(), n["decidedBy"]!.GetValue<string>()),
            _ => throw new InvalidDataException($"unknown material kind '{kind}'")
        };
    }
}

public sealed record ManualDecision(string Id, string Action, string TargetId, string Reason, string DecidedBy, long Seq);

public sealed record SessionState(
    IReadOnlyList<Material> Materials,
    Parameters Parameters,
    IReadOnlyList<ManualDecision> Decisions,
    ComputationResult Computation,
    IReadOnlyList<string> RecomputedSegments,
    (long Version, string Payload)? Freeze,
    long AtSeq);

/// <summary>Replays the event log into a session state. Replaying up to an old seq
/// restores the dependency versions and visible conclusions of that moment.</summary>
public static class Projector
{
    public const string ModelId = "core-chronoscale";

    public static SessionState Replay(IReadOnlyList<StoredEvent> events, long? upToSeq = null,
        (long Version, string Payload)? freeze = null)
    {
        var materials = new Dictionary<string, Material>(StringComparer.Ordinal);
        var order = new List<string>();
        var parameters = new Parameters();
        var decisions = new List<ManualDecision>();
        ComputationResult? previous = null;
        var recomputed = (IReadOnlyList<string>)Array.Empty<string>();
        long at = 0;

        foreach (var e in events)
        {
            if (upToSeq is { } cap && e.Seq > cap) break;
            at = e.Seq;
            var node = JsonNode.Parse(e.Payload)!;
            switch (e.Type)
            {
                case "material.imported":
                case "anchor.added":
                {
                    var m = MaterialJson.FromJson(node["material"]!);
                    if (!materials.ContainsKey(m.Id)) order.Add(m.Id);
                    materials[m.Id] = m;
                    break;
                }
                case "source.changed":
                {
                    // Local source change: supersedes one material, keeps the log intact.
                    var m = MaterialJson.FromJson(node["material"]!);
                    if (!materials.ContainsKey(m.Id)) order.Add(m.Id);
                    materials[m.Id] = m;
                    break;
                }
                case "parameters.revised":
                    parameters = new Parameters(
                        node["maxCombinations"]!.GetValue<int>(),
                        node["equalFitToleranceKa"]!.GetValue<double>(),
                        node["longTailSkewKa"]!.GetValue<double>(),
                        node["version"]!.GetValue<int>());
                    break;
                case "decision.manual":
                    decisions.Add(new ManualDecision(
                        node["id"]!.GetValue<string>(), node["action"]!.GetValue<string>(),
                        node["targetId"]!.GetValue<string>(), node["reason"]!.GetValue<string>(),
                        node["decidedBy"]!.GetValue<string>(), e.Seq));
                    break;
            }
            // Recompute incrementally after every state-changing event.
            if (e.Type is "material.imported" or "anchor.added" or "source.changed" or "parameters.revised")
            {
                var list = order.Select(id => materials[id]).ToList();
                var (result, rec) = Engine.ComputeIncremental(list, parameters, previous);
                previous = result;
                recomputed = rec;
            }
        }
        return new SessionState(order.Select(id => materials[id]).ToList(), parameters, decisions,
            previous ?? Engine.Compute(Array.Empty<Material>(), parameters), recomputed, freeze, at);
    }
}
