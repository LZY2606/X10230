namespace AgeDepthStudio.Domain;

/// <summary>Boundary kinds between core sections. Gaps and unconformities are
/// explicit boundaries: age interpolation never crosses them.</summary>
public static class BoundaryKind
{
    public const string None = "none";
    public const string Gap = "gap";
    public const string Unconformity = "unconformity";
}

public static class StratOrder
{
    public const string Normal = "normal";
    public const string Overturned = "overturned";
}

public static class MaterialKind
{
    public const string Datum = "datum";
    public const string Section = "section";
    public const string Anchor = "anchor";
    public const string EventLayer = "eventLayer";
    public const string Exclusion = "exclusion";
}

/// <summary>An immutable piece of evidence as imported. Raw representation is
/// always preserved; normalization lives in the derived layer.</summary>
public sealed record Material(
    string Id,
    string Kind,
    int Ordinal,
    string RawJson,
    string? NormJson,
    IReadOnlyList<Diagnostic> Diagnostics,
    string Source);

public sealed record DepthDatum(string Id, string Name, string Unit, string ZeroPoint, string PositiveDirection);

public sealed record Section(
    string Id,
    string CoreId,
    double TopM,
    double BottomM,
    string TopBoundary,
    string BottomBoundary,
    string StratOrder,
    string DatumId);

public sealed record AgeDist(
    string Kind,          // normal | uniform | samples
    double? MeanKa,
    double? SdKa,
    double? MinKa,
    double? MaxKa,
    double[]? SamplesKa,
    string Raw);

public sealed record Anchor(
    string Id,
    string SectionId,
    double? DepthM,        // null when the raw value could not be converted; stays in place with a diagnostic
    string DepthRaw,
    string DepthUnit,
    string DatumId,
    AgeDist Dist,
    double ReservoirOffsetKa,
    string ReservoirRaw,
    string LabCode,
    string Material);

public sealed record EventLayerCorrelation(string SectionId, double? DepthM, string DepthRaw);

public sealed record EventLayer(string Id, string Name, IReadOnlyList<EventLayerCorrelation> Correlations);

public sealed record ExclusionDecision(string Id, string TargetRef, string Reason, string DecidedBy);

/// <summary>Versioned, replaceable parameter set (a mutable decision layer).</summary>
public sealed record ParamSet(
    string Id,
    int Version,
    double RatePriorMinCmPerKa,
    double RatePriorMaxCmPerKa,
    int Iterations,
    int Seed,
    long BudgetDraws,
    int MaxCandidates);

/// <summary>Derived result for one continuous segment. Content-addressed so
/// that local recompute reuses untouched segments.</summary>
public sealed record SegmentResult(
    string Key,            // seg-<hash of geometry+anchors+param version>
    string SectionId,
    string Status,         // admissible | excluded | provisional
    string? ExclusionRule,
    string? ExclusionReason,
    double AdmissibleFraction,
    IReadOnlyList<PathPoint> MedianPath,
    IReadOnlyList<BandPoint> Band,
    IReadOnlyList<RateInterval> Rates,
    string InputsDigest,
    int ParamVersion);

public sealed record PathPoint(double DepthM, double AgeKa);

public sealed record BandPoint(double DepthM, double P025Ka, double P50Ka, double P975Ka);

public sealed record RateInterval(string SectionId, double FromDepthM, double ToDepthM,
    double P025CmPerKa, double P50CmPerKa, double P975CmPerKa);

/// <summary>A whole-core candidate model: one segment result per section.
/// Equally defensible candidates are all retained.</summary>
public sealed record ModelCandidate(
    string Id,             // mdl-<hash of member segment keys>
    IReadOnlyList<string> SegmentKeys,
    string Status,         // admissible | excluded
    string InputsDigest,
    int ParamVersion,
    long DerivedAtSeq);

public sealed record FreezeRecord(
    string Id,
    string CandidateId,
    long Seq,
    string SnapshotJson);

public sealed record EventRecord(long Seq, string Id, string Type, string PayloadJson, string CreatedAt);

public static class Rules
{
    public const string MonotonicWithinUnbrokenSegment = "age-monotonic-only-within-unbroken-segment";
    public const string ExplicitBoundaries = "gaps-and-unconformities-are-explicit-boundaries";
    public const string DistributionNotMean = "anchor-distribution-not-replaced-by-mean";
    public const string EqualCandidatesRetained = "equal-candidates-retained";
}
