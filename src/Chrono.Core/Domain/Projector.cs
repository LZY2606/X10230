using System.Text.Json;
using Chrono.Core.Models;

namespace Chrono.Core.Domain;

public static class JsonOptions
{
    public static readonly JsonSerializerOptions Opt = new(JsonSerializerDefaults.Web)
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };
}

/// <summary>Pure fold over events. Viewing branch B replays main up to B's fork
/// point plus every event on B; reading any event prefix restores that exact
/// historical dependency graph and visible conclusion (never the newest calc).</summary>
public static class Projector
{
    public static AppState Project(IEnumerable<StoredEvent> events, string? upToEventId = null, string? branch = null)
    {
        var ordered = events.OrderBy(x => x.Seq).ToList();
        long forkSeq = long.MaxValue;
        string viewBranch = branch ?? "main";

        var s = new AppState();
        bool stopped = false;
        foreach (var e in ordered)
        {
            // Branch visibility: main is always visible up to the fork; the
            // selected branch contributes only its own events.
            bool onMain = e.BranchId == "main";
            bool onView = e.BranchId == viewBranch;
            if (viewBranch != "main" && !onMain && !onView) continue;

            if (e.Type == EventType.BranchCreated)
            {
                var bc = JsonSerializer.Deserialize<BranchCreatedPayload>(e.PayloadJson, JsonOptions.Opt)!;
                // Fork point is the seq of creation event; main events after it
                // are not visible to the forked branch.
                if (bc.BranchId == viewBranch) { forkSeq = e.Seq; }
                s.Branches[bc.BranchId] = new BranchInfo(bc.BranchId, bc.Name, e.EventId, bc.ParentBranchId, bc.IsScratch, e.Seq);
            }
            else if (onMain && e.Seq > forkSeq) continue;
            if (viewBranch != "main" && onMain && e.Seq > forkSeq) continue;

            Apply(s, e);
            if (!s.Branches.ContainsKey("main"))
                s.Branches["main"] = new BranchInfo("main", "main", ordered[0].EventId, null, false, 0);

            s.EventOrder.Add(e.EventId);
            s.LastSeq = e.Seq; s.LastEventId = e.EventId;
            if (upToEventId is not null && e.EventId == upToEventId) { stopped = true; break; }
        }
        s.BranchId = viewBranch;
        return s;
    }

    private static void Apply(AppState s, StoredEvent e)
    {
        switch (e.Type)
        {
            case EventType.ProjectCreated:
            {
                var p = JsonSerializer.Deserialize<ProjectCreatedPayload>(e.PayloadJson, JsonOptions.Opt)!;
                s.ProjectId = p.ProjectId; s.Name = p.Name; s.Parameters = p.Parameters;
                s.RuleVersion = p.Parameters.Version;
                break;
            }
            case EventType.ImportBatch:
            {
                var p = JsonSerializer.Deserialize<ImportBatchPayload>(e.PayloadJson, JsonOptions.Opt)!;
                if (p.Datum is not null) s.Datum = p.Datum;
                foreach (var seg in p.Segments) s.Segments[seg.Id] = seg;
                foreach (var a in p.Anchors) s.Anchors[a.Id] = a;
                foreach (var r in p.Reservoirs) s.Reservoirs[r.Id] = r;
                foreach (var ev in p.EventLayers) s.Events[ev.Id] = ev;
                foreach (var x in p.Exclusions) s.Exclusions[x.Id] = x;
                s.Diagnostics.AddRange(p.Diagnostics);
                break;
            }
            case EventType.ParameterRevision:
            {
                var p = JsonSerializer.Deserialize<ParameterRevisionPayload>(e.PayloadJson, JsonOptions.Opt)!;
                s.Parameters = p.Parameters; s.RuleVersion = p.Parameters.Version;
                break;
            }
            case EventType.SourceRevision:
            {
                var p = JsonSerializer.Deserialize<SourceRevisionPayload>(e.PayloadJson, JsonOptions.Opt)!;
                s.Anchors[p.AnchorId] = p.RevisedAnchor;
                break;
            }
            case EventType.AnchorExcluded:
            {
                var p = JsonSerializer.Deserialize<AnchorExcludedPayload>(e.PayloadJson, JsonOptions.Opt)!;
                if (s.Anchors.TryGetValue(p.AnchorId, out var a))
                    s.Anchors[p.AnchorId] = a with { Status = AnchorStatus.Excluded, ExclusionId = p.ExclusionId };
                break;
            }
            case EventType.AnchorRestored:
            {
                var p = JsonSerializer.Deserialize<AnchorRestoredPayload>(e.PayloadJson, JsonOptions.Opt)!;
                if (s.Anchors.TryGetValue(p.AnchorId, out var a))
                    s.Anchors[p.AnchorId] = a with { Status = AnchorStatus.Active, ExclusionId = null };
                break;
            }
            case EventType.DecisionConflict:
            {
                var p = JsonSerializer.Deserialize<DecisionConflictPayload>(e.PayloadJson, JsonOptions.Opt)!;
                s.PendingConflicts.RemoveAll(c => c.DecisionId == p.DecisionId);
                break;
            }
            case EventType.ModelDerived:
            {
                var p = JsonSerializer.Deserialize<ModelDerivedPayload>(e.PayloadJson, JsonOptions.Opt)!;
                s.Candidates[p.Candidate.Id] = p.Candidate;
                break;
            }
            case EventType.ModelFrozen:
            {
                var p = JsonSerializer.Deserialize<ModelFrozenPayload>(e.PayloadJson, JsonOptions.Opt)!;
                if (s.Candidates.TryGetValue(p.CandidateId, out var c))
                {
                    s.Candidates[p.CandidateId] = c.WithFrozen(e.EventId);
                    s.FrozenCandidateId = p.CandidateId; s.FrozenAtEventId = e.EventId; s.FrozenSinceEvent = e.EventId;
                }
                break;
            }
            case EventType.ProbeRecorded:
            {
                var p = JsonSerializer.Deserialize<ProbePayload>(e.PayloadJson, JsonOptions.Opt)!;
                s.Probes.Add(p.Probe);
                break;
            }
        }
    }
}
