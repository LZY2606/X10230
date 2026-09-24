using System.Text.Json;
using AgeDepth.Domain;
using AgeDepth.Engine;

namespace AgeDepth.Store;

public sealed record FreezeOutcome(string Status, string? FreezeId, long? ActualVersion);

/// <summary>Orchestrates store + engine. All business rules live here and in the engine;
/// the browser only renders.</summary>
public sealed class AppService
{
    private static readonly JsonSerializerOptions J = new() { WriteIndented = false };
    private readonly EventStore _store;
    private readonly AgeDepthEngine _engine = new();
    private readonly object _gate = new();

    public AppService(EventStore store) => _store = store;

    public AppState State(long? atSeq = null) => Projector.Fold(_store.ReadEvents(atSeq));
    public IReadOnlyList<StoredEvent> Events(long? upTo = null) => _store.ReadEvents(upTo);
    public long ModelSetVersion => _store.ModelSetVersion;
    public Dictionary<string, (string digest, string eventId)> DerivedIndex() => _store.DerivedIndex();

    private static string Eid() => $"evt-{Guid.NewGuid():N}";

    public string Append(string type, object payload)
        => AppendMany(new[] { (type, payload) })[0];

    public List<string> AppendMany(IReadOnlyList<(string type, object payload)> events)
    {
        lock (_gate)
        {
            var ids = events.Select(e => Eid()).ToList();
            _store.AppendBatch(events.Zip(ids, (e, id) => (id, e.type, JsonSerializer.Serialize(e.payload, J))).ToList());
            return ids;
        }
    }

    /// <summary>Import an immutable material, then recompute affected derivations.</summary>
    public string ImportMaterial(string kind, object material, string? revises = null)
    {
        lock (_gate)
        {
            _store.BumpModelSet();
            var id = Append("material.imported", new { kind, material, revises });
            Recompute();
            return id;
        }
    }

    public string DefineParams(string version, double toleranceYr, string note)
    {
        lock (_gate)
        {
            _store.BumpModelSet();
            var id = Append("params.defined", new ParamSet(version, toleranceYr, note));
            Recompute();
            return id;
        }
    }

    public string RecordExclusion(ExclusionRecord rec)
    {
        lock (_gate)
        {
            _store.BumpModelSet();
            var id = Append("exclusion.recorded", rec);
            Recompute();
            return id;
        }
    }

    public string RecordDecision(string targetId, string action, string rationale, string decidedBy)
    {
        lock (_gate)
        {
            var id = Append("decision.recorded",
                new Decision($"dec-{Guid.NewGuid():N}"[..12], targetId, action, rationale, decidedBy, ""));
            return id;
        }
    }

    /// <summary>Run the engine over the current projection and record the derived result.</summary>
    public string Recompute()
    {
        lock (_gate)
        {
            var s = State();
            var input = new EngineInput(s.Datums, s.Sections, s.Boundaries, s.Anchors, s.Offsets,
                s.EventLayers, s.Exclusions, s.CurrentParams.Version, s.CurrentParams.MonotonicToleranceYr);
            var output = _engine.Compute(input);
            var view = new AppState.EngineOutputView("", output.Segments, output.GlobalDiagnostics, _store.ModelSetVersion);
            var id = Append("model.computed", view);
            view = view with { EventId = id };
            // rewrite payload with its own id for traceability
            foreach (var seg in output.Segments)
                _store.UpsertDerived(seg.SegmentKey, seg.InputDigest, seg.ParamVersion,
                    JsonSerializer.Serialize(seg, J), id);
            return id;
        }
    }

    /// <summary>Freeze the currently adopted candidates. Optimistic concurrency on the model-set version.</summary>
    public FreezeOutcome Freeze(string frozenBy, long expectedVersion)
    {
        lock (_gate)
        {
            var s = State();
            if (s.LatestCompute is null)
                return new FreezeOutcome("no-model", null, null);
            if (expectedVersion != _store.ModelSetVersion || !_store.TryAdvanceModelSet(expectedVersion))
            {
                Append("freeze.conflict", new { expected = expectedVersion, actual = _store.ModelSetVersion, by = frozenBy });
                return new FreezeOutcome("conflict", null, _store.ModelSetVersion);
            }
            var adopted = AdoptedCandidates(s);
            var points = adopted.SelectMany(c => c.Points).OrderBy(p => p.DepthM).ToList();
            var freeze = new FrozenModel($"frz-{Guid.NewGuid():N}"[..12], expectedVersion, frozenBy,
                adopted.ToDictionary(c => c.SegmentId, c => c.Id), points, s.LatestCompute.EventId);
            Append("model.frozen", freeze);
            return new FreezeOutcome("frozen", freeze.FreezeId, expectedVersion);
        }
    }

    internal static List<CandidateModel> AdoptedCandidates(AppState s)
    {
        var result = new List<CandidateModel>();
        if (s.LatestCompute is null) return result;
        foreach (var seg in s.LatestCompute.Segments)
        {
            var accepted = s.Decisions.LastOrDefault(d => d.Action == "accept" &&
                seg.Candidates.Any(c => c.Id == d.TargetId));
            var chosen = accepted is not null
                ? seg.Candidates.First(c => c.Id == accepted.TargetId)
                : seg.Candidates.FirstOrDefault();
            if (chosen is not null) result.Add(chosen);
        }
        return result;
    }

    /// <summary>Add a new anchor, recompute, and assess impact against the latest frozen model.</summary>
    public (string anchorEventId, ImpactAssessment? impact) AddAnchorWithImpact(AgeAnchor anchor)
    {
        lock (_gate)
        {
            var s = State();
            var frozen = s.Freezes.LastOrDefault();
            double? frozenMedian = null, bandBefore = null;
            string segKey = "";
            if (frozen is not null)
            {
                frozenMedian = Interp(frozen.Points, anchor.DepthM);
                bandBefore = BandAt(frozen.Points, anchor.DepthM);
            }
            var evtId = ImportMaterial("anchor", anchor);
            var s2 = State();
            ImpactAssessment? impact = null;
            if (frozen is not null && s2.LatestCompute is not null)
            {
                var seg = s2.LatestCompute.Segments.FirstOrDefault(x =>
                    anchor.DepthM >= double.Parse(x.SegmentKey.Split(':')[2].Split('-')[0]) &&
                    anchor.DepthM <= double.Parse(x.SegmentKey.Split(':')[2].Split('-')[1]));
                segKey = seg?.SegmentKey ?? "";
                var cand = seg?.Candidates.FirstOrDefault();
                double? newMedian = null, bandAfter = null;
                if (cand is not null)
                {
                    newMedian = Interp(cand.Points, anchor.DepthM);
                    bandAfter = BandAt(cand.Points, anchor.DepthM);
                }
                var within = frozenMedian is not null && anchor.Dist is not null &&
                             anchor.Dist.P5 <= frozenMedian && anchor.Dist.P95 >= frozenMedian;
                impact = new ImpactAssessment(anchor.Id, frozen.FreezeId, segKey,
                    frozenMedian, newMedian, bandBefore, bandAfter, within,
                    within ? "within-frozen-band" : "outside-frozen-band");
                Append("anchor.impact", impact);
            }
            return (evtId, impact);
        }
    }

    internal static double? Interp(IReadOnlyList<ModelPoint> pts, double depth)
    {
        if (pts.Count == 0) return null;
        var ordered = pts.OrderBy(p => p.DepthM).ToList();
        if (depth <= ordered[0].DepthM) return ordered[0].AgeP50;
        if (depth >= ordered[^1].DepthM) return ordered[^1].AgeP50;
        for (int i = 0; i + 1 < ordered.Count; i++)
            if (depth >= ordered[i].DepthM && depth <= ordered[i + 1].DepthM)
            {
                var t = (depth - ordered[i].DepthM) / (ordered[i + 1].DepthM - ordered[i].DepthM);
                return ordered[i].AgeP50 + t * (ordered[i + 1].AgeP50 - ordered[i].AgeP50);
            }
        return null;
    }

    private static double? BandAt(IReadOnlyList<ModelPoint> pts, double depth)
    {
        var nearest = pts.OrderBy(p => Math.Abs(p.DepthM - depth)).FirstOrDefault();
        return nearest is null ? null : nearest.AgeP95 - nearest.AgeP5;
    }

    public string Export() => _store.Export();
    public void Import(string json) { lock (_gate) { _store.Import(json); Recompute(); } }
}
