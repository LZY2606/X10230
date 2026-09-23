namespace DepthAge.Domain;

public sealed record StoredEvent
{
    public required long Seq { get; init; }
    public required EventEnvelope Envelope { get; init; }
    public required string Hash { get; init; }
}

/// <summary>
/// Materialized state of one branch at some event horizon. Anchors keep import order for
/// display; derivation sorts internally by (depth, id) so equivalent inputs give stable order.
/// </summary>
public sealed class BranchState
{
    public required string BranchId { get; init; }
    public string? ProjectTitle { get; set; }
    public string? CoreId { get; set; }
    public string? AgeDatum { get; set; }
    public DatumRef? Datum { get; set; }
    public List<Segment> Segments { get; } = [];
    public List<BoundarySpec> Boundaries { get; } = [];
    public List<Anchor> Anchors { get; } = [];
    public List<EventBed> EventBeds { get; } = [];
    public List<EventCorrelation> Correlations { get; } = [];
    public Dictionary<string, ReservoirModel> ReservoirModels { get; } = new();
    public Dictionary<string, RuleSet> RuleSets { get; } = new();
    public string? ActiveRuleSetVersion { get; set; }
    public List<Exclusion> Exclusions { get; } = [];
    public List<Diagnostic> Diagnostics { get; } = [];
    public List<ModelsDerived> Derivations { get; } = [];
    public List<ModelFrozen> Freezes { get; } = [];
    public List<ModelAccepted> Acceptances { get; } = [];
    public List<ModelRejected> Rejections { get; } = [];
    public List<ConflictRecorded> Conflicts { get; } = [];
    public List<PolicyAsserted> Policies { get; } = [];
    public long LastSeq { get; set; }

    public RuleSet ActiveRuleSet =>
        ActiveRuleSetVersion is not null && RuleSets.TryGetValue(ActiveRuleSetVersion, out var rs)
            ? rs
            : RuleSet.Default;

    public HashSet<string> ActiveExcludedAnchorIds =>
        Exclusions.Where(e => e.Active).Select(e => e.AnchorId).ToHashSet();

    public string? ReservoirAssignmentFor(string anchorId, Anchor anchor) => anchor.ReservoirModelId;
}

/// <summary>Folds stored events into branch states. Pure and deterministic.</summary>
public static class Projector
{
    public const string MainBranch = "main";

    public static Dictionary<string, BranchState> ProjectAll(IReadOnlyList<StoredEvent> events, long? upToSeq = null)
    {
        var branches = new Dictionary<string, BranchState>();
        foreach (var stored in events)
        {
            if (upToSeq is not null && stored.Seq > upToSeq.Value)
                break;
            Apply(branches, stored);
        }
        return branches;
    }

    public static BranchState ProjectBranch(IReadOnlyList<StoredEvent> events, string branchId, long? upToSeq = null)
    {
        var all = ProjectAll(events, upToSeq);
        if (!all.TryGetValue(branchId, out var state))
            throw new InvalidOperationException($"unknown branch '{branchId}'");
        return state;
    }

    private static void Apply(Dictionary<string, BranchState> branches, StoredEvent stored)
    {
        var env = stored.Envelope;
        var payload = env.Payload;

        if (payload is TrialBranchCreated trial)
        {
            // Fork: copy the source branch state at baseSeq, then apply the trial anchor.
            var source = ProjectAllUpTo(branches, MainBranch, trial.BaseSeq);
            var fork = CloneState(source, trial.BranchId);
            branches[trial.BranchId] = fork;
            fork.Anchors.Add(trial.TrialAnchor);
            fork.LastSeq = stored.Seq;
            return;
        }

        if (payload is BranchForked forked)
        {
            var source = ProjectAllUpTo(branches, MainBranch, forked.BaseSeq);
            var fork = CloneState(source, forked.BranchId);
            branches[forked.BranchId] = fork;
            fork.LastSeq = stored.Seq;
            return;
        }

        if (!branches.TryGetValue(env.BranchId, out var state))
        {
            state = new BranchState { BranchId = env.BranchId };
            branches[env.BranchId] = state;
        }
        state.LastSeq = stored.Seq;

        switch (payload)
        {
            case ProjectCreated p:
                state.ProjectTitle = p.Title;
                state.CoreId = p.CoreId;
                state.AgeDatum = p.AgeDatum;
                break;
            case DatumDeclared d:
                state.Datum = d.Datum;
                break;
            case SegmentsImported s:
                state.Segments.AddRange(s.Segments);
                state.Diagnostics.AddRange(s.Diagnostics);
                break;
            case BoundaryDeclared b:
                state.Boundaries.Add(b.Boundary);
                break;
            case AnchorsImported a:
                state.Anchors.AddRange(a.Anchors);
                state.Diagnostics.AddRange(a.Diagnostics);
                break;
            case EventBedsImported eb:
                state.EventBeds.AddRange(eb.Beds);
                state.Diagnostics.AddRange(eb.Diagnostics);
                break;
            case CorrelationsImported c:
                state.Correlations.AddRange(c.Correlations);
                state.Diagnostics.AddRange(c.Diagnostics);
                break;
            case ReservoirModelImported r:
                state.ReservoirModels[r.Model.Id] = r.Model;
                state.Diagnostics.AddRange(r.Diagnostics);
                break;
            case ReservoirAssignmentChanged ra:
            {
                var idx = state.Anchors.FindIndex(x => x.Id == ra.AnchorId);
                if (idx >= 0)
                    state.Anchors[idx] = state.Anchors[idx] with { ReservoirModelId = ra.ReservoirModelId };
                break;
            }
            case RuleSetPublished rp:
                state.RuleSets[rp.RuleSet.Version] = rp.RuleSet;
                state.ActiveRuleSetVersion = rp.RuleSet.Version;
                break;
            case AnchorExcluded ex:
                state.Exclusions.Add(ex.Exclusion);
                break;
            case ExclusionRevoked rev:
            {
                var idx = state.Exclusions.FindIndex(x => x.Id == rev.ExclusionId);
                if (idx >= 0)
                    state.Exclusions[idx] = state.Exclusions[idx] with
                    {
                        Active = false,
                        RevokedByEventId = env.EventId
                    };
                break;
            }
            case ModelsDerived md:
                state.Derivations.Add(md);
                break;
            case ModelFrozen mf:
                state.Freezes.Add(mf);
                break;
            case ModelAccepted ma:
                state.Acceptances.Add(ma);
                break;
            case ModelRejected mr:
                state.Rejections.Add(mr);
                break;
            case ConflictRecorded cr:
                state.Conflicts.Add(cr);
                break;
            case PolicyAsserted pa:
                state.Policies.Add(pa);
                break;
        }
    }

    private static BranchState ProjectAllUpTo(Dictionary<string, BranchState> branches, string branchId, long seq)
    {
        // Used only for forking: reconstruct the source branch from already-applied state.
        if (!branches.TryGetValue(branchId, out var state))
            throw new InvalidOperationException($"cannot fork from unknown branch '{branchId}'");
        if (state.LastSeq > seq)
            throw new InvalidOperationException($"fork base seq {seq} is behind branch horizon {state.LastSeq}");
        return state;
    }

    private static BranchState CloneState(BranchState source, string newBranchId)
    {
        var clone = new BranchState { BranchId = newBranchId };
        clone.ProjectTitle = source.ProjectTitle;
        clone.CoreId = source.CoreId;
        clone.AgeDatum = source.AgeDatum;
        clone.Datum = source.Datum;
        clone.Segments.AddRange(source.Segments);
        clone.Boundaries.AddRange(source.Boundaries);
        clone.Anchors.AddRange(source.Anchors);
        clone.EventBeds.AddRange(source.EventBeds);
        clone.Correlations.AddRange(source.Correlations);
        foreach (var kv in source.ReservoirModels) clone.ReservoirModels[kv.Key] = kv.Value;
        foreach (var kv in source.RuleSets) clone.RuleSets[kv.Key] = kv.Value;
        clone.ActiveRuleSetVersion = source.ActiveRuleSetVersion;
        clone.Exclusions.AddRange(source.Exclusions);
        clone.Diagnostics.AddRange(source.Diagnostics);
        clone.Derivations.AddRange(source.Derivations);
        clone.Freezes.AddRange(source.Freezes);
        clone.Acceptances.AddRange(source.Acceptances);
        clone.Rejections.AddRange(source.Rejections);
        clone.Conflicts.AddRange(source.Conflicts);
        clone.Policies.AddRange(source.Policies);
        clone.LastSeq = source.LastSeq;
        return clone;
    }
}
