using System.Text.Json.Nodes;

namespace GeoChron.Core;

/// <summary>
/// Projected workspace state. Layered: immutable materials (datums, segments,
/// anchors, event layers), modifiable decisions (exclusions, parameters,
/// freeze/adopt/reject), and derived models that record their dependencies.
/// </summary>
public sealed class WorkspaceState
{
    public long Seq { get; set; }
    public Dictionary<string, DepthDatum> Datums { get; } = new();
    public Dictionary<string, CoreSegment> Segments { get; } = new();
    public Dictionary<string, AgeAnchor> Anchors { get; } = new();
    public Dictionary<string, EventLayer> EventLayers { get; } = new();
    public Dictionary<string, ExclusionDecision> Exclusions { get; } = new();
    public ModelParameters Params { get; set; } = ModelParameters.Default;
    public List<ModelCandidate> Models { get; } = new();
    public List<JsonObject> Judgments { get; } = new();
    public List<Diagnostic> Diagnostics { get; } = new();
    public List<ChronEvent> Events { get; } = new();

    public bool IsActive(string anchorId) =>
        !(Exclusions.TryGetValue(anchorId, out var x) && x.Active);

    public ModelCandidate? FindModel(string id) => Models.FirstOrDefault(m => m.Id == id);

    public void Apply(ChronEvent e)
    {
        Seq = Math.Max(Seq, e.Seq);
        Events.Add(e);
        var p = e.Payload;
        switch (e.Kind)
        {
            case EventKinds.MaterialsImported:
                foreach (var d in p["datums"]!.AsArray()) { var v = JsonMap.DatumFrom(d!.AsObject()); Datums[v.Id] = v; }
                foreach (var s in p["segments"]!.AsArray()) { var v = JsonMap.SegmentFrom(s!.AsObject()); Segments[v.Id] = v; }
                foreach (var a in p["anchors"]!.AsArray()) { var v = JsonMap.AnchorFrom(a!.AsObject()); Anchors[v.Id] = v; }
                foreach (var el in p["eventLayers"]!.AsArray()) { var v = JsonMap.EventLayerFrom(el!.AsObject()); EventLayers[v.Id] = v; }
                foreach (var x in p["exclusions"]!.AsArray()) { var v = JsonMap.ExclusionFrom(x!.AsObject()); Exclusions[v.AnchorId] = v; }
                if (p["diagnostics"] is JsonArray impDiags)
                    foreach (var d in impDiags) Diagnostics.Add(JsonMap.DiagnosticFrom(d!.AsObject()));
                break;
            case EventKinds.AnchorAdded:
                {
                    var v = JsonMap.AnchorFrom(p["anchor"]!.AsObject());
                    Anchors[v.Id] = v;
                    break;
                }
            case EventKinds.AnchorExcluded:
                {
                    var anchorId = p["anchorId"]!.GetValue<string>();
                    Exclusions[anchorId] = new ExclusionDecision(
                        p["decisionId"]!.GetValue<string>(), anchorId,
                        p["reason"]!.GetValue<string>(), e.EventId, true);
                    break;
                }
            case EventKinds.AnchorReinstated:
                {
                    var anchorId = p["anchorId"]!.GetValue<string>();
                    if (Exclusions.TryGetValue(anchorId, out var x))
                        Exclusions[anchorId] = x with { Active = false };
                    break;
                }
            case EventKinds.AnchorDepthChanged:
                {
                    var anchorId = p["anchorId"]!.GetValue<string>();
                    if (Anchors.TryGetValue(anchorId, out var a))
                        Anchors[anchorId] = a with
                        {
                            DepthRaw = p["newRaw"]!.GetValue<string>(),
                            DepthM = p["newMeters"]!.GetValue<double>()
                        };
                    break;
                }
            case EventKinds.ParametersRevised:
                Params = JsonMap.ParametersFrom(p["params"]!.AsObject());
                break;
            case EventKinds.ModelsDerived:
                {
                    for (var i = 0; i < Models.Count; i++)
                        if (Models[i].Status == ModelStatus.Candidate)
                            Models[i] = Models[i] with { Status = ModelStatus.Superseded };
                    foreach (var m in p["models"]!.AsArray())
                        Models.Add(JsonMap.ModelFrom(m!.AsObject()));
                    Judgments.Clear();
                    foreach (var j in p["judgments"]!.AsArray())
                        Judgments.Add(j!.AsObject());
                    if (p["diagnostics"] is JsonArray ds)
                        foreach (var d in ds) Diagnostics.Add(JsonMap.DiagnosticFrom(d!.AsObject()));
                    break;
                }
            case EventKinds.ModelFrozen:
                SetModelStatus(p["modelId"]!.GetValue<string>(), ModelStatus.Frozen);
                break;
            case EventKinds.ModelDecided:
                SetModelStatus(
                    p["modelId"]!.GetValue<string>(),
                    p["decision"]!.GetValue<string>() == "adopt" ? ModelStatus.Adopted : ModelStatus.Rejected);
                break;
            case EventKinds.StoreRecovered:
                Diagnostics.Add(Diagnostic.Error("STORE-RECOVERED",
                    $"检测到不完整写入，已回滚到 seq={p["toSeq"]!.GetValue<long>()}", "store"));
                break;
        }
    }

    private void SetModelStatus(string modelId, string status)
    {
        for (var i = 0; i < Models.Count; i++)
            if (Models[i].Id == modelId)
                Models[i] = Models[i] with { Status = status };
    }

    /// <summary>Digest of everything that feeds derivation; used for change tracking.</summary>
    public string InputDigest()
    {
        var obj = new JsonObject
        {
            ["segments"] = new JsonArray(Segments.Values.OrderBy(s => s.Id, StringComparer.Ordinal).Select(s => (JsonNode?)JsonMap.ToJson(s)).ToArray()),
            ["anchors"] = new JsonArray(Anchors.Values.OrderBy(a => a.Id, StringComparer.Ordinal).Select(a => (JsonNode?)JsonMap.ToJson(a)).ToArray()),
            ["eventLayers"] = new JsonArray(EventLayers.Values.OrderBy(e => e.Id, StringComparer.Ordinal).Select(e => (JsonNode?)JsonMap.ToJson(e)).ToArray()),
            ["exclusions"] = new JsonArray(Exclusions.Values.OrderBy(x => x.Id, StringComparer.Ordinal).Select(x => (JsonNode?)JsonMap.ToJson(x)).ToArray()),
            ["params"] = JsonMap.ToJson(Params)
        };
        return Hashing.Sha256(Hashing.Canonical(obj));
    }
}
