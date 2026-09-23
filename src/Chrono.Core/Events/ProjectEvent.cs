using System.Text.Json;
using System.Text.Json.Serialization;
using Chrono.Core.Engine;
using Chrono.Core.Materials;

namespace Chrono.Core.Events;

public static class RuleCodes
{
    public const string MonotoneUnbroken = "RULE-MONOTONE-UNBROKEN";
    public const string GapExplicitBoundary = "RULE-GAP-EXPLICIT-BOUNDARY";
    public const string DistributionNoMean = "RULE-DISTRIBUTION-NO-MEAN";
    public const string MultipleCandidatesKept = "RULE-MULTIPLE-CANDIDATES-KEPT";
}

public enum EventKind
{
    ProjectCreated,
    MaterialsCommitted,
    ParametersRevised,
    DecisionRecorded,
    ModelComputed,
    ModelFrozen,
    AnchorProposed,
    JudgmentRecorded,
    BranchCreated
}

public enum DecisionAction { ExcludeAnchor, ReinstateAnchor, AcceptModel, RejectModel, TuneParameter, PreferCandidate }

public sealed record JudgmentPayload(
    string RuleId,
    string RuleVersion,
    string Verdict,
    IReadOnlyList<string> SubjectIds,
    string Basis,
    IReadOnlyList<string> EvidenceEventIds);

public sealed record DecisionPayload(
    string DecisionId,
    DecisionAction Action,
    string TargetId,
    string Rationale,
    string? ConflictsWithDecisionId,
    string? ProposedParametersVersion);

public sealed record ComputedModelPayload(
    string InputFingerprint,
    string ParametersVersion,
    IReadOnlyList<string> ParentEventIds,
    BuildStatus Status,
    string Summary,
    IReadOnlyList<string> CandidateIds,
    IReadOnlyList<JudgmentPayload> Judgments,
    IReadOnlyList<string> AffectedObjectIds);

public sealed record FrozenModelPayload(
    string FrozenCandidateId,
    string SourceEventId,
    string CompareAnchorId,
    string? DeltaSummary);

public sealed record MaterialsPayload(
    int Revision,
    string? SupersedesMaterialsRevisionId,
    string? ChangeDirectSourceId,
    IReadOnlyList<string> AffectedObjectIds);

public sealed record AnchorProposalPayload(
    string TempAnchorId,
    string DepthRawText,
    string AgeJson,
    string BaselineEventId);

public sealed record BranchPayload(string BranchId, string FromEventId, string Reason);

public sealed record ProjectEvent
{
    public required string Id { get; init; }
    public required string ProjectId { get; init; }
    public required string BranchId { get; init; }
    public required long Sequence { get; init; }
    public required IReadOnlyList<string> ParentEventIds { get; init; }
    public required EventKind Kind { get; init; }
    public required string PayloadJson { get; init; }
    public required string Actor { get; init; }
    public required string RuleSetVersion { get; init; }
    public required DateTimeOffset CreatedAtUtc { get; init; }
    public string? Label { get; init; }
    public bool Frozen { get; init; }

    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = false,
        DefaultIgnoreCondition = JsonIgnoreCondition.Never,
        Converters = { new JsonStringEnumConverter() }
    };

    public T Payload<T>() where T : class =>
        JsonSerializer.Deserialize<T>(PayloadJson, Options)
        ?? throw new InvalidOperationException("null payload");

    public static ProjectEvent Create<T>(
        string id, string projectId, string branchId, long sequence,
        IEnumerable<string> parents, EventKind kind, T payload,
        string actor, string ruleSetVersion, DateTimeOffset now, string? label = null, bool frozen = false)
        where T : class => new()
    {
        Id = id,
        ProjectId = projectId,
        BranchId = branchId,
        Sequence = sequence,
        ParentEventIds = parents.ToArray(),
        Kind = kind,
        PayloadJson = JsonSerializer.Serialize(payload, Options),
        Actor = actor,
        RuleSetVersion = ruleSetVersion,
        CreatedAtUtc = now,
        Label = label,
        Frozen = frozen
    };
}

public sealed record DerivedSnapshot(
    string AtEventId,
    string BranchId,
    MaterialProject? Materials,
    ChronoParameters Parameters,
    IReadOnlyList<SourceExclusion> DecisionExclusions,
    EngineBuildResult? Build,
    IReadOnlyList<string> FrozenCandidateIds,
    IReadOnlyList<ProjectEvent> History);
