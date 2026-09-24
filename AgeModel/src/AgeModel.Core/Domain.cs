using System.Text.Json.Serialization;

namespace AgeModel.Core;

public sealed record CasePackage(
    string Schema,
    string CaseId,
    string CaseName,
    int PackageVersion,
    string Narrative,
    DepthDatum DepthDatum,
    IReadOnlyList<CoreSegment> Segments,
    IReadOnlyList<StratigraphicBoundary> Boundaries,
    IReadOnlyList<ReservoirOffset> ReservoirOffsets,
    IReadOnlyList<AgeAnchor> Anchors,
    IReadOnlyList<EventLayer> EventLayers,
    IReadOnlyList<ExclusionDecision> Exclusions,
    RuleParameters Parameters,
    IReadOnlyList<ReplayStep> ReplaySteps);

public sealed record DepthDatum(
    string Id,
    string Name,
    string CoordinateDatum,
    string DepthUnit,
    string PositiveDirection,
    string RawReference,
    string? EffectiveFromDepth = null,
    string? EffectiveToDepth = null);

public sealed record CoreSegment(
    string Id,
    string Name,
    DomainQuantity StartDepth,
    DomainQuantity EndDepth,
    string Lithology,
    IReadOnlyList<string> SourceQuoteIds);

public sealed record StratigraphicBoundary(
    string Id,
    string BoundaryKind,
    DomainQuantity TopDepth,
    DomainQuantity BaseDepth,
    string Description,
    bool AllowsAgeInterpolation,
    DomainQuantity? MinimumMissingAge = null,
    DomainQuantity? MaximumMissingAge = null);

public sealed record DomainQuantity(
    string Raw,
    string Unit,
    string? Datum = null,
    double? NormalizedValue = null,
    string? NormalizedUnit = null,
    string? Diagnostic = null)
{
    public bool IsConvertible => NormalizedValue.HasValue && string.IsNullOrEmpty(Diagnostic);
}

public sealed record EmpiricalDistribution(
    string Id,
    string Kind,
    string RawRepresentation,
    string AgeUnit,
    string TimeDatum,
    IReadOnlyList<double> Samples,
    IReadOnlyList<double> Quantiles,
    IReadOnlyList<double> QuantileProbabilities,
    string? Diagnostic = null);

public sealed record AgeAnchor(
    string Id,
    string Label,
    DomainQuantity Depth,
    EmpiricalDistribution AgeDistribution,
    string Method,
    string Material,
    string SourceId,
    string? ReservoirOffsetId = null,
    string? EventCorrelationId = null,
    bool AlternativeAtSameDepth = false);

public sealed record ReservoirOffset(
    string Id,
    string Label,
    DomainQuantity Offset,
    string AppliesToMethod,
    string TimeDatum,
    string Policy,
    IReadOnlyList<string> TargetAnchorIds,
    string Reason);

public sealed record EventLayer(
    string Id,
    string Label,
    string EventKind,
    DomainQuantity Depth,
    EmpiricalDistribution? AgeDistribution,
    string CorrelationId,
    IReadOnlyList<string> LinkedAnchorIds,
    string SourceId,
    string CorrelationBasis);

public sealed record ExclusionDecision(
    string Id,
    string SubjectType,
    string SubjectId,
    string Status,
    string Reason,
    string DecidedBy,
    string AtRuleVersion,
    IReadOnlyList<string> EvidenceIds);

public sealed record RuleParameters(
    string Version,
    string ParentVersion,
    bool AllowZeroSedimentationRate,
    int MinimumAnchorsPerInterval,
    int MaximumCandidates,
    double LongTailProbability,
    string ReservoirPolicy,
    string MonotonicDirection,
    int MaxDistributionSamples,
    string Reason);

public sealed record ReplayStep(
    string Id,
    string StepKind,
    string Label,
    string Description,
    string TargetId,
    string? DirectSourceId,
    JsonElement Payload);

public sealed record ProjectBundle(
    string BundleSchema,
    string ProjectId,
    string ExportedAt,
    string CaseId,
    IReadOnlyList<EventEnvelope> Events,
    string OriginalPackageJson);

public enum ProjectStatus
{
    Ok = 0,
    SemanticallyUnsolvable = 1,
    ResourceBudgetExhausted = 2,
    FormatCorrupt = 3
}
