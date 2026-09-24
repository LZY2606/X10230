using System.Text.Json.Serialization;

namespace CoreChronology;

public enum Severity { Info, Warning, Error }
public enum GapKind { SamplingGap, Hiatus, Unconformity }
public enum CandidateStatus { Open, Accepted, RejectedByRule, RejectedByDecision, Frozen, ConflictBlocked }
public enum DecisionDisposition { Accept, Reject, AdjustParameters, Freeze, Compare, BlockedByFreeze }
public enum OutcomeKind { Success, IncompleteInput, FormatCorrupt, SemanticNoSolution, ResourceBudgetExhausted, FrozenConflict, ConcurrencyConflict }

public sealed record Diagnostic(
    string Code,
    Severity Severity,
    string Location,
    string Message,
    IReadOnlyDictionary<string, string>? Facts = null);

public sealed record Quantity(
    string Text,
    string Unit,
    string? DatumId = null,
    string? CoordinateReference = null,
    double? NumericValue = null,
    string? NormalizedUnit = null,
    double? NormalizedValue = null,
    IReadOnlyList<Diagnostic>? Diagnostics = null)
{
    public bool CanUse => NormalizedValue.HasValue && Severity != Severity.Error && !Diagnostics.Any(d => d.Severity == Severity.Error);
}

public sealed record AgeDistribution(
    string Id,
    string Kind,
    string TimeBasis,
    string Unit,
    string RawText,
    IReadOnlyList<double> Samples,
    IReadOnlyDictionary<string, double> Quantiles,
    IReadOnlyList<Diagnostic>? Diagnostics = null)
{
    public bool HasUsableSamples => Samples.Count > 0 && !Diagnostics.Any(d => d.Severity == Severity.Error);
}

public sealed record CoreSection(
    string Id,
    string Label,
    Quantity TopDepth,
    Quantity BottomDepth,
    string MaterialId,
    int Version,
    string SourceId);

public sealed record DepthGap(
    string Id,
    string Label,
    GapKind Kind,
    Quantity UpperDepth,
    Quantity LowerDepth,
    bool AllowsAgeInterpolation,
    string MaterialId,
    int Version,
    string SourceId,
    string Basis);

public sealed record ReservoirOffset(
    string Id,
    string Label,
    AgeDistribution Distribution,
    string MaterialId,
    int Version,
    string SourceId,
    string Basis);

public sealed record AgeAnchor(
    string Id,
    string Label,
    Quantity Depth,
    AgeDistribution RawAge,
    string? ReservoirOffsetId,
    AgeDistribution? CorrectedAge,
    string EventLayerId,
    string MaterialId,
    int Version,
    string SourceId,
    bool DirectSource,
    string? RevisionOfMaterialId = null);

public sealed record EventLayer(
    string Id,
    string Label,
    Quantity Depth,
    string CorrelationBasis,
    IReadOnlyList<string> CorrelatedAnchorIds,
    string MaterialId,
    int Version,
    string SourceId);

public sealed record ExclusionDecision(
    string Id,
    string SubjectMaterialId,
    string Disposition,
    string Reason,
    string SourceId,
    string MaterialId,
    int Version);

public sealed record RuleParameters(
    string Id,
    int Version,
    double MonotonicIntervalProbability,
    Quantity TieTolerance,
    string ReservoirPolicy,
    string Basis,
    string? SupersedesParameterId = null);

public sealed record MaterialBundle(
    IReadOnlyList<CoreSection> Sections,
    IReadOnlyList<DepthGap> Gaps,
    IReadOnlyList<ReservoirOffset> ReservoirOffsets,
    IReadOnlyList<AgeAnchor> Anchors,
    IReadOnlyList<EventLayer> EventLayers,
    IReadOnlyList<ExclusionDecision> Exclusions,
    RuleParameters Parameters);

public sealed record ConstraintEvaluation(
    string Id,
    string EventId,
    string CandidateId,
    string RuleCode,
    string Status,
    string Basis,
    IReadOnlyList<string> SubjectIds,
    IReadOnlyList<string> AffectedObjectIds,
    string? BoundaryId = null,
    string? MachinePredicate = null);

public sealed record RateInterval(
    string Id,
    string SegmentId,
    string UpperAnchorId,
    string LowerAnchorId,
    double Lower,
    double Median,
    double Upper,
    string Unit,
    IReadOnlyList<string> ParentAnchorIds,
    string ParameterVersionId);

public sealed record UncertaintyBandPoint(
    string Id,
    string SegmentId,
    double Depth,
    string DepthUnit,
    double Young,
    double Median,
    double Old,
    string AgeUnit,
    string TimeBasis,
    IReadOnlyList<string> ParentAnchorIds);

public sealed record SegmentModel(
    string SegmentId,
    string Status,
    IReadOnlyList<string> AnchorIds,
    IReadOnlyList<RateInterval> Rates,
    IReadOnlyList<UncertaintyBandPoint> Band,
    IReadOnlyList<string> BoundaryIds);

public sealed record CandidateModel(
    string Id,
    string EventId,
    string BranchId,
    int Rank,
    int Score,
    bool Tie,
    CandidateStatus Status,
    string InputSummary,
    string ParameterVersionId,
    IReadOnlyList<string> ParentEventIds,
    IReadOnlyDictionary<string, string> MaterialVersions,
    IReadOnlyList<SegmentModel> Segments,
    IReadOnlyList<ConstraintEvaluation> Evaluations,
    string? DecisionId = null);

public sealed record ChronoEvent(
    string Id,
    int Sequence,
    string BranchId,
    string Type,
    string Title,
    string At,
    IReadOnlyList<string> ParentEventIds,
    string? ParameterVersionId,
    string InputSummary,
    string PayloadJson,
    string? FrozenCandidateId = null,
    int? FreezeAtSequence = null);

public sealed record ManualDecision(
    string Id,
    string EventId,
    string CandidateId,
    string BranchId,
    DecisionDisposition Disposition,
    string Rationale,
    string ExpectedParentEventId,
    string Status,
    string? ConflictingDecisionId = null);

public sealed record DisplayValue(
    string Id,
    string Kind,
    string Label,
    string Value,
    string Unit,
    string? Basis = null,
    string? ValidRange = null,
    string? InvalidReason = null,
    IReadOnlyList<string>? ParentIds = null);

public sealed record ProjectView(
    string ProjectId,
    string Title,
    int Revision,
    string? FrozenCandidateId,
    MaterialBundle Materials,
    IReadOnlyList<ChronoEvent> Events,
    IReadOnlyList<CandidateModel> Candidates,
    IReadOnlyList<ManualDecision> Decisions,
    IReadOnlyList<ConstraintEvaluation> Evaluations,
    IReadOnlyList<DisplayValue> DisplayValues,
    IReadOnlyList<Diagnostic> Diagnostics);

public sealed record OperationResult(
    OutcomeKind Outcome,
    ProjectView? View,
    IReadOnlyList<Diagnostic> Diagnostics,
    string? EventId = null)
{
    public bool Ok => Outcome == OutcomeKind.Success;
}
