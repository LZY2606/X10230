using System.Text.Json.Serialization;

namespace ChronoCore;

public enum SessionRole { Work = 0, Baseline = 1, Experiment = 2 }

public sealed record SessionHeader(
    string Id,
    string Label,
    SessionRole Role,
    string? ParentSessionId,
    string? ForkedFromEventId,
    string? ScenarioCode,
    long CreatedAtUnixMs);

/// <summary>Append-only, hash-chained domain event. Everything visible in the
/// review UI is a fold of these records; recomputation never overwrites the
/// historical events (time travel restores the exact past conclusion).</summary>
public sealed record EventRecord
{
    public required long Seq { get; init; }
    public required string Id { get; init; }
    public required string SessionId { get; init; }
    public required string Type { get; init; }
    public required string Actor { get; init; }
    public required long OccurredAtUnixMs { get; init; }
    public required string PayloadJson { get; init; }
    /// <summary>SHA-256 of the canonical serialization of engine inputs, or
    /// null for pure decision events.</summary>
    public string? InputDigest { get; init; }
    public string? ParamVersion { get; init; }
    public string? DerivationId { get; init; }
    /// <summary>Explicit causation graph: which events this one derives from.</summary>
    public required IReadOnlyList<string> ParentEventIds { get; init; }
    public string RuleVersion { get; init; } = ParamSet.RuleVersion;
    /// <summary>Hash chain over (previous event hash + content).</summary>
    public required string ChainHash { get; init; }
}

// ----- Payloads ------------------------------------------------------------

public sealed record ImportPayload(
    string SourceLabel,
    string BundleId,
    int DatumCount, int SegmentCount, int BreakCount, int AnchorCount,
    int EventBedCount, int ExclusionCount, int UnresolvedCount,
    string ParamVersion,
    IReadOnlyList<Diagnostic> Diagnostics);

public sealed record ParamRevisionPayload(
    string NewVersion,
    string? ParentVersion,
    double MinRateCmPerKa,
    double MaxRateCmPerKa,
    double MinMonotoneDrawFraction,
    int DrawBudget,
    int MaxCandidates,
    string Note,
    IReadOnlyList<string> RecomputedAnchorIds,
    string DerivationId);

/// <summary>Exactly one direct source is touched per counterexample step.</summary>
public sealed record SourceMaterialChangedPayload(
    string ChangeCode,
    string TargetKind,
    string TargetId,
    string Field,
    string OldRepresentation,
    string NewRepresentation,
    string InputFragment,
    IReadOnlyList<string> DependencyScopeAnchorIds,
    IReadOnlyList<string> AffectedCandidateIds,
    string DerivationId,
    bool LocalRecomputeOnly);

public enum HumanDecisionAction { Exclude = 0, Reinstate = 1, AcceptCandidate = 2, RejectCandidate = 3, SetPreference = 4 }

public sealed record HumanDecisionPayload(
    string DecisionId,
    HumanDecisionAction Action,
    string TargetKind,
    string TargetId,
    string ReasonCode,
    string Reason,
    string DecidedBy,
    bool Revoked,
    string? ConflictsWithDecisionId,
    string Resolution,
    string? DerivationId);

public sealed record DerivationPayload(
    string DerivationId,
    DerivationStatus Status,
    string InputDigest,
    string ParamVersion,
    int CandidateCount,
    int RejectedCount,
    int DrawCount,
    int DrawBudget,
    IReadOnlyList<CandidateSummary> CandidateSummaries,
    IReadOnlyList<Finding> Findings,
    IReadOnlyList<Diagnostic> Diagnostics,
    IReadOnlyList<string> RecomputedAnchorIds,
    IReadOnlyList<string> ParentDerivationIds);

public sealed record CandidateSummary(string CandidateId, int Rank, double Score, bool Tie, string? TieGroupId, bool Rejected, string? RejectReason);

public sealed record ModelFrozenPayload(
    string FreezeId,
    string CandidateId,
    string Label,
    string DerivationId,
    string InputDigest,
    string ParamVersion);

public sealed record NewAnchorTrialPayload(
    string TrialId,
    string TrialAnchorId,
    double DepthCm,
    string DepthRaw,
    string DistributionBasis,
    string? ComparedCandidateId,
    IReadOnlyList<string> ImpactedAnchorIds,
    IReadOnlyList<string> ImpactedSegmentIds,
    double? ShiftAtTopCalYear,
    string? BlockedReason,
    string TrialDerivationId);

public sealed record TrialDispositionPayload(
    string TrialId,
    bool Accepted,
    string Reason,
    string? NewDerivationId);

public sealed record FreezeConflictPayload(
    string TrialId,
    string FreezeId,
    string CandidateId,
    string Reason,
    string ConcurrentActor,
    bool SecondAttemptRejected);

public sealed record ExperimentVerdictPayload(
    string ScenarioCode,
    string ScenarioLabel,
    string ChangedDirectSource,
    bool ImpactConfined,
    IReadOnlyList<string> ExpectedScopeIds,
    IReadOnlyList<string> ActualScopeIds,
    IReadOnlyList<string> AdoptedCandidateIds,
    IReadOnlyList<string> RejectedCandidateIds,
    IReadOnlyList<RuleJudgement> Judgements,
    string ResultStatus);
