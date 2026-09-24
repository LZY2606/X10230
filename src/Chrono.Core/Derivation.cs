namespace Chrono.Core;

public enum DeriveStatus { Ok, IncompleteInput, SemanticUnsolvable, BudgetExhausted }

public sealed record DeriveDiagnostic(
    string Code,         // DEPTH_DUPLICATE | AGE_INVERSION | UNCONFORMITY | GAP | INCOMPLETE | ...
    string Severity,     // error | warning
    string Location,     // material id / coordinate
    string Message,
    IReadOnlyList<string> AffectedIds);

/// <summary>One monotonic run of depth-age curve, bounded above and below by explicit surfaces.</summary>
public sealed record ModelSection(
    string Id,
    double TopDepthM,
    double BaseDepthM,
    string TopBoundary,    // continuous | gap | unconformity | core-top | core-base
    string BottomBoundary,
    bool MonotonicValid,
    string? InvalidReason,
    IReadOnlyList<string> AnchorIds);

/// <summary>Sedimentation rate for an interval. Intervals crossing a gap/unconformity are flagged.</summary>
public sealed record RateInterval(
    string Id,
    string SectionId,
    double FromDepthM,
    double ToDepthM,
    double MedianMPerKa,
    double Q05MPerKa,
    double Q95MPerKa,
    bool Valid,
    string? InvalidReason);

public sealed record BandPoint(
    string Id,
    string SectionId,
    double DepthM,
    double MedianCalBP,
    double Q05CalBP,
    double Q95CalBP);

public sealed record CandidateModel
{
    public string Id { get; init; } = "";
    public string VariantLabel { get; init; } = "";
    public IReadOnlyList<string> ChosenAnchorIds { get; init; } = Array.Empty<string>();
    public double ScoreSigma { get; init; }
    public bool EquallyOptimal { get; init; }
    public bool Adopted { get; init; }
    public IReadOnlyList<ModelSection> Sections { get; init; } = Array.Empty<ModelSection>();
    public IReadOnlyList<RateInterval> Rates { get; init; } = Array.Empty<RateInterval>();
    public IReadOnlyList<BandPoint> Band { get; init; } = Array.Empty<BandPoint>();
}

public sealed record DerivationResult
{
    public string ResultId { get; init; } = "";
    public string InputDigest { get; init; } = "";
    public string ParameterVersion { get; init; } = "";
    public IReadOnlyList<string> ParentResultIds { get; init; } = Array.Empty<string>();
    public string MaterialRevisionId { get; init; } = "";
    public DeriveStatus Status { get; init; }
    public IReadOnlyList<DeriveDiagnostic> Diagnostics { get; init; } = Array.Empty<DeriveDiagnostic>();
    public IReadOnlyList<CandidateModel> Candidates { get; init; } = Array.Empty<CandidateModel>();
    public IReadOnlyList<Finding> Findings { get; init; } = Array.Empty<Finding>();
    public int DrawsUsed { get; init; }
    public bool BudgetExhausted { get; init; }
}
