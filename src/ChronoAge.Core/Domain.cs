using System.Text.Json.Serialization;

namespace ChronoAge.Core;

public enum OutcomeStatus
{
    Success,
    CompletedWithDiagnostics,
    IncompleteInput,
    FormatCorrupt,
    SemanticImpossible,
    BudgetExhausted,
    Conflict
}

public enum BoundaryKind
{
    Continuous,
    DepthGap,
    Unconformity,
    Fault,
    InvertedInterval
}

public enum MaterialStatus
{
    Included,
    Excluded,
    Unconverted,
    Superseded
}

public enum CandidateDisposition
{
    Legal,
    Rejected,
    Accepted,
    Superseded
}

public sealed record Diagnostic(
    string Code,
    string Severity,
    string Path,
    string Message,
    IReadOnlyList<string> AffectedObjectIds,
    string? RawValue = null)
{
    public bool IsBlocking => Severity == "error";
}

public sealed record MeasuredValue
{
    public string Raw { get; init; } = "";
    public string Unit { get; init; } = "";
    public string? Datum { get; init; }
    public double? Value { get; init; }
    public string? NormalizedUnit { get; init; }
    public string? ConversionDiagnostic { get; init; }

    public MeasuredValue() { }

    public MeasuredValue(string raw, string unit, string? datum = null, double? value = null,
        string? normalizedUnit = null, string? conversionDiagnostic = null)
    {
        Raw = raw;
        Unit = unit;
        Datum = datum;
        Value = value;
        NormalizedUnit = normalizedUnit;
        ConversionDiagnostic = conversionDiagnostic;
    }
}

public sealed record AgeDistribution
{
    public string DistributionId { get; init; } = "";
    public string Type { get; init; } = "empirical-quantiles";
    public string Unit { get; init; } = "";
    public string TimeDatum { get; init; } = "";
    public Dictionary<string, double> Quantiles { get; init; } = new();
    public IReadOnlyList<double>? Samples { get; init; }
    public string? LongTailNote { get; init; }
}

public sealed record ReservoirOffset
{
    public string OffsetId { get; init; } = "";
    public MeasuredValue Value { get; init; } = new();
    public string TimeDatum { get; init; } = "";
    public string? Basis { get; init; }
}

public sealed record DepthDatum
{
    public string Id { get; init; } = "";
    public string Name { get; init; } = "";
    public string Code { get; init; } = "";
    public MeasuredValue Elevation { get; init; } = new();
    public string CoordinateReference { get; init; } = "";
}

public sealed record CoreSegment
{
    public string Id { get; init; } = "";
    public string Label { get; init; } = "";
    public MeasuredValue TopDepth { get; init; } = new();
    public MeasuredValue BottomDepth { get; init; } = new();
    public BoundaryKind LowerBoundary { get; init; }
    public double? GapAfter { get; init; }
    public string? GapUnit { get; init; }
    public MaterialStatus Status { get; init; } = MaterialStatus.Included;
    public string? SourceFragment { get; init; }
    public string? Note { get; init; }
}

public sealed record AgeAnchor
{
    public string Id { get; init; } = "";
    public string Label { get; init; } = "";
    public MeasuredValue Depth { get; init; } = new();
    public AgeDistribution Distribution { get; init; } = new();
    public ReservoirOffset? ReservoirOffset { get; init; }
    public IReadOnlyList<string> EventCorrelationIds { get; init; } = Array.Empty<string>();
    public MaterialStatus Status { get; init; } = MaterialStatus.Included;
    public string SourceId { get; init; } = "";
    public string? SourceFragment { get; init; }
    public string? ExcludedReason { get; init; }
}

public sealed record EventLayer
{
    public string Id { get; init; } = "";
    public string Label { get; init; } = "";
    public MeasuredValue Depth { get; init; } = new();
    public string? CorrelationKey { get; init; }
    public string? CorrelatedAnchorId { get; init; }
    public MaterialStatus Status { get; init; } = MaterialStatus.Included;
    public string SourceId { get; init; } = "";
    public string? SourceFragment { get; init; }
    public string? Note { get; init; }
}

public sealed record ExclusionDecision
{
    public string Id { get; init; } = "";
    public string ObjectId { get; init; } = "";
    public string Reason { get; init; } = "";
    public string SourceFragment { get; init; } = "";
    public bool ImmutableMaterialNote { get; init; }
}

public sealed record ChronologyPackage
{
    public string PackageId { get; init; } = "";
    public string SchemaVersion { get; init; } = "1";
    public string Title { get; init; } = "";
    public string SourceRevisionId { get; init; } = "";
    public string? SupersedesRevisionId { get; init; }
    public string ParametersVersion { get; init; } = "params-v1";
    public List<DepthDatum> DepthDatums { get; init; } = new();
    public List<CoreSegment> Segments { get; init; } = new();
    public List<AgeAnchor> Anchors { get; init; } = new();
    public List<EventLayer> EventLayers { get; init; } = new();
    public List<ExclusionDecision> Exclusions { get; init; } = new();
}

public sealed record ChronologyParameters
{
    public string Version { get; init; } = "params-v1";
    public int CandidateBudget { get; init; } = 64;
    public bool PermitUnconformityAgeJump { get; init; } = true;
    public bool PermitGapAgeJump { get; init; } = true;
}

public sealed record NormalizedAnchor
{
    public required string Id { get; init; }
    public required string Label { get; init; }
    public required double DepthM { get; init; }
    public required double Q05Year { get; init; }
    public required double Q50Year { get; init; }
    public required double Q95Year { get; init; }
    public required double Q99Year { get; init; }
    public required string SegmentId { get; init; }
    public required string SourceId { get; init; }
    public required AgeAnchor Raw { get; init; }
    public bool HasLongTail => Q99Year - Q95Year > Q50Year - Q05Year;
}

public sealed record RateInterval
{
    public required string Id { get; init; }
    public required string FromAnchorId { get; init; }
    public required string ToAnchorId { get; init; }
    public required double DepthIntervalM { get; init; }
    public required double MinYearPerM { get; init; }
    public required double MedianYearPerM { get; init; }
    public required double MaxYearPerM { get; init; }
    public required string Unit { get; init; }
    public required string ValidRange { get; init; }
}

public sealed record UncertaintyBandPoint
{
    public required string Id { get; init; }
    public required double DepthM { get; init; }
    public required double Q05Year { get; init; }
    public required double Q50Year { get; init; }
    public required double Q95Year { get; init; }
}

public sealed record ModelBlock
{
    public required string Id { get; init; }
    public required string SegmentId { get; init; }
    public required IReadOnlyList<string> AnchorIds { get; init; }
}

public sealed record AgeModelCandidate
{
    public required string Id { get; init; }
    public required int StableOrder { get; init; }
    public required double Score { get; init; }
    public required bool EquallyOptimal { get; init; }
    public required CandidateDisposition Disposition { get; init; }
    public required IReadOnlyList<string> SelectedAnchorIds { get; init; }
    public required IReadOnlyList<ModelBlock> Blocks { get; init; }
    public required IReadOnlyList<RateInterval> Rates { get; init; }
    public required IReadOnlyList<UncertaintyBandPoint> Band { get; init; }
    public required IReadOnlyList<string> Diagnostics { get; init; }
}

public sealed record StructuralBoundary
{
    public required string Id { get; init; }
    public required string SegmentId { get; init; }
    public required BoundaryKind Kind { get; init; }
    public required double DepthM { get; init; }
    public required string Reason { get; init; }
    public required string DepthDatum { get; init; }
}

public sealed record DisplayValue
{
    public required string Id { get; init; }
    public required string Label { get; init; }
    public required string RawValue { get; init; }
    public required string Unit { get; init; }
    public string? Datum { get; init; }
    public string? NormalizedValue { get; init; }
    public string? ValidRange { get; init; }
    public string? FailureReason { get; init; }
    public required string SourceEventId { get; init; }
    public required string SourceMaterialId { get; init; }
}

public sealed record AgeModelResult
{
    public required OutcomeStatus Status { get; init; }
    public required string InputDigest { get; init; }
    public required string ParametersVersion { get; init; }
    public required string RuleVersion { get; init; }
    public required IReadOnlyList<Diagnostic> Diagnostics { get; init; }
    public required IReadOnlyList<StructuralBoundary> Boundaries { get; init; }
    public required IReadOnlyList<AgeModelCandidate> Candidates { get; init; }
    public required IReadOnlyList<string> AffectedObjectIds { get; init; }
    public required IReadOnlyList<DisplayValue> DisplayValues { get; init; }
    public required IReadOnlyList<string> ParentDependencyIds { get; init; }
}
