namespace ChronoCore.Domain;

// ---------------------------------------------------------------------------
// Derived (normalized) layer. Computed, never imported directly.
// ---------------------------------------------------------------------------

public sealed record NormalizedSegment(string MaterialId, double TopM, double BottomM, string Facies);

public sealed record NormalizedAnchor(
    string MaterialId,
    double DepthM,
    AgeDistribution Age,       // canonical yr BP, reservoir-corrected
    double ReservoirOffsetYr);

public sealed record NormalizedBoundary(string MaterialId, double DepthM, string Kind, string? MissingInterval);

public sealed record NormalizedEventBed(string MaterialId, double DepthM, string Name, string? CorrelatesTo);

public sealed record RunSpan(int RunIndex, double TopM, double BottomM);

public sealed record TieGroup(string Key, double DepthM, IReadOnlyList<string> AnchorIds, string Resolution);

public sealed record NormalizedProfile(
    string CoreId,
    string Datum,
    IReadOnlyList<NormalizedSegment> Segments,
    IReadOnlyList<NormalizedAnchor> Anchors,
    IReadOnlyList<NormalizedBoundary> Boundaries,
    IReadOnlyList<NormalizedEventBed> EventBeds,
    IReadOnlyList<RunSpan> Runs,
    IReadOnlyList<TieGroup> TieGroups,
    IReadOnlyList<Diagnostic> Diagnostics);

/// <summary>Versioned rule set: every derivation records which parameter version produced it.</summary>
public sealed record RuleSet(
    string Version,
    int MaxSamples,              // resource budget for Monte-Carlo envelope work
    double TieToleranceM,        // anchors closer than this share a depth
    double MonotonicToleranceYr, // allowed numerical slack
    double LongTailSkewLimit,    // tail skew above which a long-tail path is flagged
    IReadOnlyList<string> Kernels,
    int GridCount)
{
    public static readonly RuleSet V1 = new(
        "rules-v1",
        MaxSamples: 5_000,
        TieToleranceM: 0.02,
        MonotonicToleranceYr: 1.0,
        LongTailSkewLimit: 3.0,
        new[] { "linear", "monotone-pchip" },
        GridCount: 80);

    public static readonly RuleSet V2 = V1 with
    {
        Version = "rules-v2",
        MaxSamples = 2_000,                 // tightened budget in the parameter revision
        TieToleranceM = 0.01,
        LongTailSkewLimit = 2.5
    };
}
