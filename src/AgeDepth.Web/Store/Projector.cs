using System.Text.Json;
using AgeDepth.Domain;

namespace AgeDepth.Store;

public sealed record ParamSet(string Version, double MonotonicToleranceYr, string Note);
public sealed record Decision(string Id, string TargetId, string Action, string Rationale, string DecidedBy, string AtEventId);

/// <summary>Fully projected application state at a point in the event history.</summary>
public sealed class AppState
{
    public List<DepthDatum> Datums { get; } = new();
    public List<CoreSection> Sections { get; } = new();
    public List<StratBoundary> Boundaries { get; } = new();
    public List<AgeAnchor> Anchors { get; } = new();
    public List<ReservoirOffset> Offsets { get; } = new();
    public List<EventLayer> EventLayers { get; } = new();
    public List<ExclusionRecord> Exclusions { get; } = new();
    public List<ParamSet> Params { get; } = new();
    public List<Decision> Decisions { get; } = new();
    public List<FrozenModel> Freezes { get; } = new();
    public List<ImpactAssessment> Impacts { get; } = new();
    public EngineOutputView? LatestCompute { get; set; }
    public long LastSeq { get; set; }
    public ParamSet CurrentParams => Params.Count > 0 ? Params[^1] : new ParamSet("params/v1", 25.0, "default");

    public sealed record EngineOutputView(string EventId, IReadOnlyList<SegmentResult> Segments,
        IReadOnlyList<Diagnostic> GlobalDiagnostics, long ModelSetVersion);
}

/// <summary>Folds events into state. Replaying up to event N restores the dependency
/// versions and visible conclusions of that moment — history is never overwritten.</summary>
public static class Projector
{
    private static readonly JsonSerializerOptions J = new() { PropertyNameCaseInsensitive = true };

    public static AppState Fold(IEnumerable<StoredEvent> events)
    {
        var s = new AppState();
        foreach (var e in events)
        {
            s.LastSeq = e.Seq;
            var p = e.Payload;
            switch (e.Type)
            {
                case "material.imported" or "material.revised":
                    AddMaterial(s, J.Parse(p)); break;
                case "params.defined":
                    s.Params.Add(JsonSerializer.Deserialize<ParamSet>(p, J)!); break;
                case "exclusion.recorded":
                    s.Exclusions.Add(JsonSerializer.Deserialize<ExclusionRecord>(p, J)!); break;
                case "decision.recorded":
                    s.Decisions.Add(JsonSerializer.Deserialize<Decision>(p, J)!); break;
                case "model.computed":
                    s.LatestCompute = JsonSerializer.Deserialize<AppState.EngineOutputView>(p, J); break;
                case "model.frozen":
                    s.Freezes.Add(JsonSerializer.Deserialize<FrozenModel>(p, J)!); break;
                case "anchor.impact":
                    s.Impacts.Add(JsonSerializer.Deserialize<ImpactAssessment>(p, J)!); break;
            }
        }
        return s;
    }

    private static void AddMaterial(AppState s, JsonElement el)
    {
        // Materials are immutable in the event log; a revision (same Id) supersedes
        // the earlier projection value while the original event is preserved.
        string kind = el.GetProperty("kind").GetString()!;
        string json = el.GetProperty("material").GetRawText();
        switch (kind)
        {
            case "datum": Upsert(s.Datums, JsonSerializer.Deserialize<DepthDatum>(json, J)!, x => x.Id); break;
            case "section": Upsert(s.Sections, JsonSerializer.Deserialize<CoreSection>(json, J)!, x => x.Id); break;
            case "boundary": Upsert(s.Boundaries, JsonSerializer.Deserialize<StratBoundary>(json, J)!, x => x.Id); break;
            case "anchor": Upsert(s.Anchors, JsonSerializer.Deserialize<AgeAnchor>(json, J)!, x => x.Id); break;
            case "offset": Upsert(s.Offsets, JsonSerializer.Deserialize<ReservoirOffset>(json, J)!, x => x.Id); break;
            case "eventlayer": Upsert(s.EventLayers, JsonSerializer.Deserialize<EventLayer>(json, J)!, x => x.Id); break;
        }
    }

    private static void Upsert<T>(List<T> list, T item, Func<T, string> id)
    {
        int i = list.FindIndex(x => id(x) == id(item));
        if (i >= 0) list[i] = item; else list.Add(item);
    }
}
