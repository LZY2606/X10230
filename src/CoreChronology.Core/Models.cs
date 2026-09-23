using System.Text.Json;

namespace CoreChronology.Core;

public sealed record MeasuredQuantity(
    double? Value,
    string Unit,
    string? Datum,
    string RawRepresentation,
    IReadOnlyList<Diagnostic> Diagnostics)
{
    public bool IsConvertible => Value.HasValue && Diagnostics.Count == 0;
}

public sealed record Diagnostic(
    string Code,
    string Severity,
    string Location,
    string Message,
    string? RawRepresentation)
{
    public const string FormatCorrupt = "format.corrupt";
    public const string SemanticUnresolvable = "semantic.unresolvable";
    public const string IncompleteInput = "input.incomplete";
    public const string BudgetExhausted = "resource.budget_exhausted";
    public const string FrozenConflict = "model.frozen_conflict";
}

public sealed record DepthDatum(
    string Id,
    string CoreId,
    string Direction,
    string Unit,
    string Reference,
    string OriginRaw,
    string Notes);

public sealed record SegmentGap(
    string Id,
    string SegmentIdAbove,
    string SegmentIdBelow,
    MeasuredQuantity Gap,
    string BoundaryKind,
    string Rationale);

public sealed record CoreSegment(
    string Id,
    string Label,
    MeasuredQuantity StartDepth,
    MeasuredQuantity EndDepth,
    bool Continuous,
    IReadOnlyList<string> EventLayerIds);

public sealed record AgeDistribution(
    string SamplesUnit,
    string TimeDatum,
    IReadOnlyList<double> Samples,
    IReadOnlyDictionary<string, double> Quantiles,
    bool LongTail)
{
    public bool HasMean => false;
}

public sealed record RadiocarbonAge(
    string Id,
    string MaterialType,
    MeasuredQuantity Depth,
    AgeDistribution Age,
    string? ReservoirModelId,
    string? ReservoirCorrectedSamplesUnit,
    IReadOnlyList<double>? ReservoirCorrectedSamples,
    bool Excluded,
    string? ExclusionReason,
    IReadOnlyList<Diagnostic> Diagnostics);

public sealed record ReservoirCorrection(
    string Id,
    string Label,
    MeasuredQuantity Offset,
    string Applicability,
    int Version,
    bool Active);

public sealed record EventLayer(
    string Id,
    string Label,
    MeasuredQuantity Depth,
    string CorrelationKind,
    AgeDistribution? CorrelatedAge,
    IReadOnlyList<string> Evidence,
    bool Excluded,
    string? ExclusionReason);

public sealed record ExclusionDecision(
    string Id,
    string SubjectId,
    string SubjectKind,
    bool Excluded,
    string Reason,
    string Source,
    string? RuleCode);

public sealed record ParameterVersion(
    string Id,
    int Version,
    string Method,
    double MonotonicTailProbability,
    double CandidateScoreToleranceYears,
    int MaxCandidates,
    int MaxDerivationSteps,
    string Rationale,
    bool Active);

public sealed record RateInterval(
    string Id,
    string SegmentId,
    string AboveAnchorId,
    string BelowAnchorId,
    double DepthFromM,
    double DepthToM,
    double MedianCmPerKa,
    double Q05CmPerKa,
    double Q95CmPerKa,
    string Unit,
    string DepthDatum,
    string TimeDatum);

public sealed record CandidateAgeModel(
    string Id,
    string StableLineageId,
    string SegmentId,
    int Rank,
    double ScoreYears,
    bool EquallyPreferred,
    string Status,
    IReadOnlyList<string> SelectedAnchorIds,
    IReadOnlyList<string> EventLayerIds,
    IReadOnlyList<RateInterval> Rates,
    IReadOnlyList<UncertaintyBandPoint> UncertaintyBand,
    IReadOnlyList<Diagnostic> Diagnostics)
{
    public bool IsLegal => Status == "legal" || Status == "unconstrained";
}

public sealed record UncertaintyBandPoint(
    string Id,
    string CandidateId,
    double DepthM,
    double Q05CalKaBP,
    double Q50CalKaBP,
    double Q95CalKaBP,
    string DepthDatum,
    string TimeDatum);

public sealed record Derivation(
    string Id,
    string ParameterVersionId,
    string InputDigest,
    IReadOnlyList<string> ParentDerivationIds,
    IReadOnlyList<string> AffectedSegmentIds,
    IReadOnlyList<CandidateAgeModel> Candidates,
    IReadOnlyList<Diagnostic> Diagnostics,
    string Reason,
    DateTimeOffset CreatedAt);

public sealed record ProjectEvent(
    string Id,
    int Sequence,
    string BranchId,
    string ParentEventId,
    string Type,
    string SubjectId,
    string Summary,
    string InputDigest,
    string? DerivationId,
    IReadOnlyList<string> AcceptedCandidateIds,
    IReadOnlyList<string> RejectedCandidateIds,
    bool Freeze,
    string? FrozenStableLineageId,
    string CreatedAt,
    JsonElement Payload);

public sealed record ProjectState(
    string ProjectId,
    string HeadEventId,
    int Sequence,
    string BranchId,
    DepthDatum DepthDatum,
    IReadOnlyList<CoreSegment> Segments,
    IReadOnlyList<SegmentGap> Gaps,
    IReadOnlyList<RadiocarbonAge> Anchors,
    IReadOnlyList<ReservoirCorrection> Reservoirs,
    IReadOnlyList<EventLayer> Events,
    IReadOnlyList<ExclusionDecision> Exclusions,
    IReadOnlyList<ParameterVersion> Parameters,
    Derivation? CurrentDerivation,
    IReadOnlyList<ProjectEvent> Events2,
    bool Frozen,
    string? FrozenStableLineageId,
    IReadOnlyList<DisplayValue> DisplayValues);

public sealed record DisplayValue(
    string Id,
    string Kind,
    string SubjectId,
    string Label,
    string Value,
    string Unit,
    string? Datum,
    double? NumericValue,
    string? ValidFromEventId,
    string? InvalidReason);

public sealed record CounterfactualTrial(
    string Id,
    string Label,
    string ChangedDirectSourceId,
    string ChangeDescription,
    string Trigger,
    string BaselineDigest,
    string ResultDigest,
    bool Adopted,
    string Decision,
    string Rationale,
    IReadOnlyList<string> InputFragmentIds,
    IReadOnlyList<string> AffectedObjectIds,
    IReadOnlyList<string> ContainedObjectIds,
    IReadOnlyList<ProjectEvent> Events2,
    IReadOnlyList<Diagnostic> Diagnostics);

public sealed record MachineJudgement(
    string Id,
    string Code,
    string TruthValue,
    string Basis,
    IReadOnlyList<string> EvidenceIds,
    string Scope,
    string EventId);
