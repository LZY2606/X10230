using System.Text.Json;

namespace ChronoCore;

/// <summary>State reconstructed purely by replaying the append-only event log.</summary>
public sealed class Snapshot
{
    public MaterialsDoc Materials;
    public ParameterSet Parameters = new();
    public Dictionary<string, AgeModelCandidate> Candidates = new();
    public HashSet<string> Excluded = new();
    public List<ExclusionDecision> ExclusionHistory = new();
    public string FrozenCandidateId;
    public long FrozenSeq;
    public Anchor TrialAnchor;
    public Dictionary<string, object> TrialImpact;
    public int Generation;
    public List<ChronoEvent> Events = new();
    public List<Judgment> LatestJudgments = new();
    public BuildResult LatestBuild;
    public List<Diagnostic> ActiveDiagnostics = new();
}

public static class Replayer
{
    private static readonly JsonSerializerOptions J = SqliteStore.JsonOpts;

    public static Snapshot Replay(IReadOnlyList<ChronoEvent> events, long? upToSeq = null)
    {
        var snap = new Snapshot();
        foreach (var ev in events)
        {
            if (upToSeq.HasValue && ev.Seq > upToSeq.Value) break;
            snap.Events.Add(ev);
            Apply(snap, ev);
        }
        if (snap.Materials != null)
        {
            snap.ActiveDiagnostics = snap.Materials.Diagnostics.ToList();
            AgeModelEngine.OrderSegments(snap.Materials, snap.ActiveDiagnostics);
            foreach (var a in snap.Materials.Anchors)
                if (!a.Depth.IsValid || !a.RawAge.IsValid)
                    snap.ActiveDiagnostics.Add(new Diagnostic
                    {
                        Code = DiagnosticCodes.Unconvertible,
                        Location = $"$.anchors[{a.Id}]",
                        Message = !a.Depth.IsValid ? a.Depth.Diagnostics : a.RawAge.Diagnostics
                    });
        }
        return snap;
    }

    private static T Pay<T>(ChronoEvent ev)
        => JsonSerializer.Deserialize<T>(ev.Payload.GetRawText(), J);

    private static void Apply(Snapshot snap, ChronoEvent ev)
    {
        switch (ev.Type)
        {
            case EventType.MaterialsImported:
                snap.Materials = Pay<MaterialsDoc>(ev);
                break;
            case EventType.SourceChanged:
            {
                var changed = Pay<SourceChangePayload>(ev);
                if (snap.Materials != null && changed.Materials != null) snap.Materials = changed.Materials;
                snap.Generation++;
                break;
            }
            case EventType.ParameterRevised:
            {
                var p = Pay<ParameterSet>(ev);
                snap.Parameters = p;
                snap.Generation++;
                break;
            }
            case EventType.ModelBuilt:
            {
                var build = Pay<BuildResult>(ev);
                snap.LatestBuild = build;
                snap.LatestJudgments = build.GlobalJudgments;
                foreach (var c in build.Candidates)
                    snap.Candidates[c.Id] = c;
                break;
            }
            case EventType.ModelFrozen:
            {
                var f = Pay<FreezePayload>(ev);
                snap.FrozenCandidateId = f.CandidateId;
                snap.FrozenSeq = ev.Seq;
                if (snap.Candidates.TryGetValue(f.CandidateId, out var c))
                {
                    c.Status = CandidateStatus.Frozen;
                    c.FrozenByEventSeq = ev.Seq.ToString();
                }
                foreach (var kv in snap.Candidates)
                    if (kv.Key != f.CandidateId && kv.Value.Status != CandidateStatus.Frozen)
                        kv.Value.Status = CandidateStatus.Superseded;
                break;
            }
            case EventType.FreezeRejected:
                break;
            case EventType.DecisionExclude:
            {
                var d = Pay<ExclusionDecision>(ev);
                snap.ExclusionHistory.Add(d);
                if (d.Excluded) snap.Excluded.Add(d.SubjectId);
                else snap.Excluded.Remove(d.SubjectId);
                break;
            }
            case EventType.DecisionPolicy:
            {
                var pl = Pay<PolicyPayload>(ev);
                snap.ExclusionHistory.Add(new ExclusionDecision
                {
                    Id = pl.DecisionId, SubjectType = "policy", SubjectId = pl.SubjectId,
                    Excluded = pl.Action == "exclude", Reason = pl.Reason, DecidedBy = ev.Actor
                });
                if (pl.Action == "exclude") snap.Excluded.Add(pl.SubjectId);
                else snap.Excluded.Remove(pl.SubjectId);
                break;
            }
            case EventType.TrialApplied:
            {
                var t = Pay<TrialPayload>(ev);
                snap.TrialAnchor = t.Anchor;
                snap.TrialImpact = t.Impact;
                break;
            }
            case EventType.TrialAccepted:
            {
                snap.TrialAnchor = null;
                snap.TrialImpact = null;
                break;
            }
            case EventType.TrialRejected:
                snap.TrialAnchor = null;
                snap.TrialImpact = null;
                break;
        }
    }
}

public sealed class SourceChangePayload
{
    public string ChangedDirectSource { get; set; }
    public List<string> ChangedAnchorIds { get; set; } = new();
    public MaterialsDoc Materials { get; set; }
    public BuildResult Build { get; set; }
}

public sealed class FreezePayload
{
    public string CandidateId { get; set; }
    public string TieKey { get; set; }
    public List<string> RetainedAlternativeIds { get; set; } = new();
}

public sealed class PolicyPayload
{
    public string DecisionId { get; set; }
    public string SubjectId { get; set; }
    public string Action { get; set; }          // exclude | include
    public string Reason { get; set; }
    public string ConflictWithDecisionId { get; set; }
    public bool Adopted { get; set; }
    public BuildResult Build { get; set; }
}

public sealed class TrialPayload
{
    public Anchor Anchor { get; set; }
    public BuildResult Build { get; set; }
    public Dictionary<string, object> Impact { get; set; }
    public string Decision { get; set; }        // applied | accepted | rejected
}
