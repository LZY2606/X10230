using System.Text.Json;
using System.Text.Json.Serialization;
using Chrono.Core.Engine;
using Chrono.Core.Events;
using Chrono.Core.Materials;

namespace Chrono.Storage.Workflow;

public enum WorkflowFailure { None, BranchMissing, BranchFrozen, ConcurrentWrite, NoMaterials, Corrupt }

public sealed record WorkflowResult(WorkflowFailure Failure, ProjectEvent? Event, EngineBuildResult? Build)
{
    public bool Ok => Failure == WorkflowFailure.None;
}

public sealed class WorkflowService
{
    private readonly ProjectStore _store;
    private readonly IChronoEngine _engine;
    private readonly string _ruleSetVersion = "ruleset-2026.1";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = false,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Converters = { new JsonStringEnumConverter() }
    };

    public WorkflowService(ProjectStore store, IChronoEngine engine)
    {
        _store = store;
        _engine = engine;
    }

    public string RuleSetVersion => _ruleSetVersion;

    public ProjectedState StateAt(string branchId, string? atEventId = null)
    {
        var events = _store.ReadEvents(branchId);
        return atEventId == null
            ? MaterialProjector.Fold(events)
            : MaterialProjector.At(events, atEventId);
    }

    public WorkflowResult CommitMaterials(string projectId, string branchId, MaterialProject project,
        int revision, string? supersedesEventId, string? directSourceId, string actor,
        IReadOnlyList<string> affectedObjectIds, string? label = null)
    {
        var parents = supersedesEventId == null
            ? Array.Empty<string>()
            : new[] { supersedesEventId };
        var envelope = new MaterialsSnapshotEnvelope(project);
        var payload = new MaterialsPayload(revision, supersedesEventId, directSourceId, affectedObjectIds);
        return AppendWithSnapshot(branchId, seq => ProjectEvent.Create(
            StableIds.Event(projectId, "materials", $"{revision}:{string.Join(",", affectedObjectIds)}"),
            projectId, branchId, seq, parents, EventKind.MaterialsCommitted, payload,
            actor, _ruleSetVersion, DateTimeOffset.UtcNow, label ?? $"materials r{revision}"),
            seq => (JsonSerializer.Serialize(envelope, JsonOptions), (string?)null));
    }

    public WorkflowResult ReviseParameters(string projectId, string branchId,
        ChronoParameters parameters, string parentEventId, string actor, string label)
    {
        return AppendWithSnapshot(branchId, seq => ProjectEvent.Create(
            StableIds.Event(projectId, "params", parameters.Version),
            projectId, branchId, seq, new[] { parentEventId },
            EventKind.ParametersRevised, new ParametersEnvelope(parameters),
            actor, _ruleSetVersion, DateTimeOffset.UtcNow, label),
            seq => (null, null));
    }

    public WorkflowResult RecordDecision(string projectId, string branchId,
        DecisionPayload decision, string parentEventId, string actor)
    {
        return AppendWithSnapshot(branchId, seq => ProjectEvent.Create(
            StableIds.Event(projectId, "decision", decision.DecisionId),
            projectId, branchId, seq, new[] { parentEventId },
            EventKind.DecisionRecorded, decision, actor, _ruleSetVersion,
            DateTimeOffset.UtcNow, $"decision:{decision.Action}"),
            seq => (null, null));
    }

    public WorkflowResult RecordJudgmentOnly(string projectId, string branchId,
        JudgmentPayload judgment, string parentEventId, string actor, string label)
    {
        return AppendWithSnapshot(branchId, seq => ProjectEvent.Create(
            StableIds.Event(projectId, "judgment", judgment.RuleId + ":" + string.Join(",", judgment.SubjectIds)),
            projectId, branchId, seq, new[] { parentEventId },
            EventKind.JudgmentRecorded, judgment, actor, _ruleSetVersion,
            DateTimeOffset.UtcNow, label),
            seq => (null, null));
    }

    public WorkflowResult Compute(string projectId, string branchId, BuildBudget? budget,
        string actor, string? label = null)
    {
        var state = StateAt(branchId);
        if (state.Materials == null)
            return new WorkflowResult(WorkflowFailure.NoMaterials, null, null);

        var merged = MergeDecisionExclusions(state.Materials, state.DecisionExclusions);
        var inputs = new BuildInputs(merged, state.Parameters, budget ?? BuildBudget.Default);
        var result = _engine.Build(inputs);

        var judgments = StandardJudgments(result, merged, state.Parameters);
        var affected = AffectedObjects(merged, result);
        var parentEvent = state.Events.LastOrDefault();
        var payload = new ComputedModelPayload(
            result.Fingerprint.Hash, state.Parameters.Version,
            parentEvent == null ? Array.Empty<string>() : new[] { parentEvent.Id },
            result.Status, result.Report.Summary,
            result.Candidates.Select(c => c.CandidateId).ToArray(),
            judgments, affected);

        var outcome = AppendWithSnapshot(branchId, seq => ProjectEvent.Create(
            StableIds.Event(projectId, "compute", result.Fingerprint.Hash + ":" + state.Parameters.Version + ":" + seq),
            projectId, branchId, seq, payload.ParentEventIds,
            EventKind.ModelComputed, payload, actor, _ruleSetVersion,
            DateTimeOffset.UtcNow, label ?? $"compute {result.Status}"),
            seq => (null, JsonSerializer.Serialize(new DerivedCacheEnvelope(result), JsonOptions)));
        return new WorkflowResult(outcome.Failure switch
        {
            WorkflowFailure.None => WorkflowFailure.None,
            WorkflowFailure.BranchFrozen => WorkflowFailure.BranchFrozen,
            WorkflowFailure.BranchMissing => WorkflowFailure.BranchMissing,
            _ => WorkflowFailure.ConcurrentWrite
        }, outcome.Event, result);
    }

    public EngineBuildResult? BuildAt(string branchId, string eventId)
    {
        var state = StateAt(branchId, eventId);
        if (state.Materials == null) return null;
        var merged = MergeDecisionExclusions(state.Materials, state.DecisionExclusions);
        return _engine.Build(new BuildInputs(merged, state.Parameters, BuildBudget.Default));
    }

    public IReadOnlyList<JudgmentPayload> StandardJudgments(EngineBuildResult result,
        MaterialProject project, ChronoParameters parameters)
    {
        var list = new List<JudgmentPayload>();

        var chains = result.Chains;
        list.Add(new JudgmentPayload(
            RuleCodes.MonotoneUnbroken, _ruleSetVersion,
            result.Status == BuildStatus.SemanticallyUnsolvable ? "reject" : "satisfied",
            chains.Select(c => c.ChainId).ToList(),
            "age must be non-increasing with depth only inside each unbroken segment",
            chains.SelectMany(c => c.ViolationCodes.Where(v => v.Contains("INVERTED"))
                .Select(_ => c.ChainId)).Distinct().ToList()));

        list.Add(new JudgmentPayload(
            RuleCodes.GapExplicitBoundary, _ruleSetVersion,
            project.Boundaries.Any(b => b.Kind is Core.Materials.BoundaryKind.Unconformity
                or Core.Materials.BoundaryKind.CoringGap) ? "bounded" : "no-gaps-present",
            project.Boundaries.Select(b => b.Id).ToList(),
            "depth gaps and unconformities are explicit boundaries; no interpolation crosses them",
            project.Boundaries.Select(b => b.Id).ToList()));

        var longTailAnchors = project.Anchors
            .Where(a => a.MeasuredAge.IsLongTail())
            .Select(a => a.Id).ToList();
        list.Add(new JudgmentPayload(
            RuleCodes.DistributionNoMean, _ruleSetVersion,
            longTailAnchors.Count > 0 ? "distributions-preserved-longtail" : "distributions-preserved",
            project.Anchors.Select(a => a.Id).ToList(),
            "an anchor distribution is never replaced by its mean; quantiles are propagated through sampling",
            longTailAnchors));

        var tied = result.Candidates.Where(c => c.Tied).Select(c => c.CandidateId).Distinct().ToList();
        list.Add(new JudgmentPayload(
            RuleCodes.MultipleCandidatesKept, _ruleSetVersion,
            tied.Count > 1 ? "multiple-equally-good-kept" : "single-ranking-or-none",
            result.Candidates.Select(c => c.CandidateId).ToList(),
            "equally defensible candidates are all retained with stable ordering",
            tied));

        return list;
    }

    private static IReadOnlyList<string> AffectedObjects(MaterialProject project, EngineBuildResult result)
    {
        var ids = new List<string>();
        ids.AddRange(result.Candidates.Select(c => c.CandidateId));
        ids.AddRange(result.Chains.Select(c => c.ChainId));
        ids.AddRange(project.Anchors.Select(a => a.Id));
        return ids.Distinct().ToList();
    }

    public static MaterialProject MergeDecisionExclusions(
        MaterialProject project, IReadOnlyList<SourceExclusion> decisionExclusions)
    {
        var byAnchor = decisionExclusions.GroupBy(e => e.TargetAnchorId)
            .ToDictionary(g => g.Key, g => g.Last());
        var merged = project.Exclusions
            .Where(e => !byAnchor.ContainsKey(e.TargetAnchorId) ||
                        byAnchor[e.TargetAnchorId].Reason.StartsWith("REINSTATE", StringComparison.Ordinal))
            .ToList();
        var reinstated = new HashSet<string>(decisionExclusions
            .Where(e => e.Reason.StartsWith("REINSTATE", StringComparison.Ordinal))
            .Select(e => e.TargetAnchorId));
        merged.RemoveAll(e => reinstated.Contains(e.TargetAnchorId) && e.HumanDecision);
        merged.AddRange(decisionExclusions.Where(e =>
            !e.Reason.StartsWith("REINSTATE", StringComparison.Ordinal)));
        return project with { Exclusions = merged };
    }

    public WorkflowResult Freeze(string projectId, string branchId, string candidateId,
        string sourceEventId, string? compareAnchorId, string? deltaSummary, string actor)
    {
        var payload = new FrozenModelPayload(candidateId, sourceEventId,
            compareAnchorId ?? "", deltaSummary);
        var outcome = AppendWithSnapshot(branchId, seq => ProjectEvent.Create(
            StableIds.Event(projectId, "freeze", candidateId + ":" + branchId),
            projectId, branchId, seq, new[] { sourceEventId },
            EventKind.ModelFrozen, payload, actor, _ruleSetVersion,
            DateTimeOffset.UtcNow, $"freeze:{candidateId}", frozen: true),
            seq => (null, null));
        if (outcome.Ok) _store.FreezeBranch(branchId, true);
        return outcome;
    }

    public string Branch(string projectId, string newBranchId, string fromEventId, string reason, string actor)
        => _store.CreateBranch(newBranchId, projectId, fromEventId, reason, actor, _ruleSetVersion, DateTimeOffset.UtcNow);

    public DerivedSnapshot? Snapshot(string branchId, string? atEventId)
    {
        var state = StateAt(branchId, atEventId);
        if (state.Materials == null) return null;
        var merged = MergeDecisionExclusions(state.Materials, state.DecisionExclusions);
        var build = _engine.Build(new BuildInputs(merged, state.Parameters, BuildBudget.Default));
        var headEvent = state.Events.Last();
        return new DerivedSnapshot(headEvent.Id, branchId, merged, state.Parameters,
            state.DecisionExclusions, build, state.FrozenCandidateIds, state.Events);
    }

    private WorkflowResult AppendWithSnapshot(string branchId,
        Func<long, ProjectEvent> eventFactory,
        Func<long, (string? MaterialsJson, string? DerivedJson)> snapshotFactory)
    {
        var seq = _store.NextSequence(branchId);
        var e = eventFactory(seq);
        var (materialsJson, derivedJson) = snapshotFactory(seq);
        var outcome = _store.Append(e, seq, Array.Empty<string>());
        if (!outcome.Ok)
            return new WorkflowResult(outcome.Failure switch
            {
                AppendFailure.BranchFrozen => WorkflowFailure.BranchFrozen,
                AppendFailure.BranchMissing => WorkflowFailure.BranchMissing,
                _ => WorkflowFailure.ConcurrentWrite
            }, null, null);
        if (materialsJson != null)
            _store.SaveMaterialSnapshot(e.Id, branchId, seq, materialsJson);
        if (derivedJson != null)
            _store.SaveDerivedCache(e.Id, branchId, seq, derivedJson);
        return new WorkflowResult(WorkflowFailure.None, e, null);
    }
}

public sealed record DerivedCacheEnvelope(EngineBuildResult Build);
