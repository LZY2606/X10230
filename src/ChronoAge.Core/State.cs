using System.Text.Json;

namespace ChronoAge.Core;

/// <summary>
/// Materialized state from replaying an event prefix. State up to sequence N is exactly what
/// was visible after event N: dependency versions and conclusions are restored, not replaced
/// by newer computations.
/// </summary>
public sealed class ProjectState
{
    public string ProjectId { get; set; } = "";
    public string Name { get; set; } = "";
    public string RuleVersion { get; set; } = "";
    public string CasePackId { get; set; } = "";
    public string CasePackVersion { get; set; } = "";
    public string BranchId { get; set; } = "main";

    /// <summary>logId -> current (highest version, not superseded) material.</summary>
    public Dictionary<string, Material> Current { get; } = new();
    /// <summary>id -> every material version ever seen (historical provenance).</summary>
    public Dictionary<string, Material> AllVersions { get; } = new();
    public List<EventRecord> Events { get; } = new();
    public List<EventRecord> Decisions { get; } = new();

    public ModelParameters Parameters { get; set; } = ModelParameters.Default;
    public string? ParametersVersionInEffect { get; set; }

    public HashSet<string> Excluded { get; } = new();
    public Dictionary<string, OverlapResolved> OverlapCalls { get; } = new();
    public Dictionary<string, BoundaryInterpreted> BoundaryCalls { get; } = new();
    public Dictionary<string, TieResolved> TieCalls { get; } = new();
    public HashSet<string> AcceptedCandidates { get; } = new();
    public HashSet<string> RejectedCandidates { get; } = new();
    public List<FrozenModel> Frozen { get; } = new();
    public bool IsAnyFrozen => Frozen.Any(f => f.Active);

    public IEnumerable<Material> ActiveMaterials => Current.Values.Where(m => !Excluded.Contains(m.LogId));

    public T? LatestOfType<T>() where T : Material =>
        Current.Values.OfType<T>().FirstOrDefault(m => !Excluded.Contains(m.LogId));

    public IEnumerable<T> ActiveOfType<T>() where T : Material =>
        Current.Values.OfType<T>().Where(m => !Excluded.Contains(m.LogId));

    public IEnumerable<CoreSection> OrderedSections() =>
        ActiveOfType<CoreSection>()
            .Where(s => s.Top.Canonical.HasValue && s.Bottom.Canonical.HasValue)
            .OrderBy(s => s.Top.Canonical!.Value)
            .ThenBy(s => s.Id, StringComparer.Ordinal);

    public IEnumerable<SegmentBoundary> ActiveBoundaries() => ActiveOfType<SegmentBoundary>();

    public static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true
    };
}

public sealed record FrozenModel(
    string FrozenModelId,
    string CandidateId,
    string DerivationId,
    IReadOnlyList<string> DependencyIds,
    string AtUtc,
    bool Active,
    string? UnfreezeReason);

/// <summary>Replays journal events into states, detecting conflicting human decisions.</summary>
public static class StateReplayer
{
    public static ProjectState Replay(IEnumerable<EventRecord> events, string branchId = "main", long? upToSeq = null)
    {
        var st = new ProjectState { BranchId = branchId };
        foreach (var ev in events.OrderBy(e => e.Seq))
        {
            if (upToSeq.HasValue && ev.Seq > upToSeq.Value) break;
            if (ev.BranchId != branchId) continue;
            Apply(st, ev);
            st.Events.Add(ev);
            if (EventKinds.DecisionKinds.Contains(ev.Kind)) st.Decisions.Add(ev);
        }
        return st;
    }

    /// <summary>Apply one event and return any conflicts produced (for the service to persist).</summary>
    public static IReadOnlyList<ConflictRecorded> Apply(ProjectState st, EventRecord ev)
    {
        var conflicts = new List<ConflictRecorded>();
        var p = JsonSerializer.Deserialize<EventPayload>(ev.PayloadJson, ProjectState.JsonOpts);
        switch (p)
        {
            case ProjectCreated pc:
                st.ProjectId = pc.ProjectId; st.Name = pc.Name; st.RuleVersion = pc.RuleVersion;
                st.CasePackId = pc.CasePackId; st.CasePackVersion = pc.CasePackVersion;
                break;
            case MaterialImported mi:
                foreach (var m in mi.Materials) { st.AllVersions[m.Id] = m; st.Current[m.LogId] = m; }
                break;
            case MaterialRevised mr:
                foreach (var m in mr.NewVersions)
                {
                    if (st.Current.TryGetValue(m.LogId, out var old))
                        st.AllVersions[old.Id] = old with { SupersededBy = m.Id };
                    st.AllVersions[m.Id] = m;
                    st.Current[m.LogId] = m;
                }
                break;
            case ParametersSet ps:
                st.Parameters = ps.Parameters;
                st.ParametersVersionInEffect = ps.Parameters.Version;
                break;
            case ExclusionDecided ex:
                if (ex.Excluded) st.Excluded.Add(ex.MaterialLogId); else st.Excluded.Remove(ex.MaterialLogId);
                break;
            case OverlapResolved ov:
                if (st.OverlapCalls.TryGetValue(ov.BoundaryLogId, out var prev) && prev.Choice != ov.Choice)
                    conflicts.Add(MakeConflict(ev, ov.BoundaryLogId, $"overlap choice '{prev.Choice}' then '{ov.Choice}'"));
                st.OverlapCalls[ov.BoundaryLogId] = ov;
                break;
            case BoundaryInterpreted bi:
                if (st.BoundaryCalls.TryGetValue(bi.BoundaryLogId, out var prevb) && prevb.Interpretation != bi.Interpretation)
                    conflicts.Add(MakeConflict(ev, bi.BoundaryLogId, $"interpretation '{prevb.Interpretation}' then '{bi.Interpretation}'"));
                st.BoundaryCalls[bi.BoundaryLogId] = bi;
                break;
            case TieResolved tr:
                var key = string.CompareOrdinal(tr.AnchorA, tr.AnchorB) <= 0
                    ? $"{tr.AnchorA}|{tr.AnchorB}" : $"{tr.AnchorB}|{tr.AnchorA}";
                if (st.TieCalls.TryGetValue(key, out var prevt) && prevt.Order != tr.Order)
                    conflicts.Add(MakeConflict(ev, key, $"tie order '{prevt.Order}' then '{tr.Order}'"));
                st.TieCalls[key] = tr;
                break;
            case CandidateAccepted ca:
                st.AcceptedCandidates.Add(ca.CandidateId);
                if (st.RejectedCandidates.Contains(ca.CandidateId))
                    conflicts.Add(MakeConflict(ev, ca.CandidateId, "candidate first rejected then accepted"));
                break;
            case CandidateRejected cr:
                st.RejectedCandidates.Add(cr.CandidateId);
                if (st.AcceptedCandidates.Contains(cr.CandidateId))
                    conflicts.Add(MakeConflict(ev, cr.CandidateId, "candidate first accepted then rejected"));
                break;
            case ModelFrozen mf:
                st.Frozen.Add(new FrozenModel(mf.FrozenModelId, mf.CandidateId, mf.DerivationId, mf.DependencyIds, mf.AtUtc, true, null));
                break;
            case ModelUnfrozen mu:
                for (var i = 0; i < st.Frozen.Count; i++)
                    if (st.Frozen[i].FrozenModelId == mu.FrozenModelId)
                        st.Frozen[i] = st.Frozen[i] with { Active = false, UnfreezeReason = mu.Reason };
                break;
        }
        return conflicts;
    }

    private static ConflictRecorded MakeConflict(EventRecord ev, string scope, string what) =>
        new($"CF{ev.Seq:D6}", "EV?", ev.EventId, scope, $"conflicting_decisions:{what}|both_retained");

}
