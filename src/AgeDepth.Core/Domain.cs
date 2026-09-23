namespace AgeDepth.Core;

/// <summary>Computation outcomes. BudgetExhausted / FormatCorrupt / SemanticallyUnsolvable are three distinct failures.</summary>
public enum ResultStatus { Ok, BudgetExhausted, FormatCorrupt, SemanticallyUnsolvable }

public sealed record Diagnostic(
    string Code, string Severity, string Message,
    string? AnchorId = null, double? DepthM = null, string? SegmentId = null);

public sealed record Anchor(string Id, string CoreId, Quantity Depth, AgeDistribution Dist, string Raw, string Source);

/// <summary>An explicit section boundary. Kind: "gap" (missing section) or "unconformity" (hiatus surface).</summary>
public sealed record Boundary(string Id, string CoreId, double TopM, double BottomM, string Kind);

public sealed record EventLayer(string Id, string CoreId, Quantity Depth, string EventName, AgeDistribution EventAge, string Raw);

public sealed record ModelParams(
    int EnsembleSize = 64,
    int MaxCandidates = 5,
    int MaxAnchorsPerSegment = 40,
    int Version = 1);

public sealed record ModelInput(
    IReadOnlyList<Anchor> Anchors,
    IReadOnlyList<Boundary> Boundaries,
    double CoreTopM,
    double CoreBottomM,
    IReadOnlySet<string> ExcludedAnchorIds,
    IReadOnlyDictionary<string, double> ReservoirOffsetsBySource);

public sealed record Knot(double DepthM, double AgeKa);

public sealed record CandidatePath(string Id, Knot[] Knots, double Weight, string Status, string? Reason);

public sealed record BandPoint(double DepthM, double P05, double P50, double P95);

public sealed record RateInterval(string SegmentId, double TopM, double BottomM,
    double MinMPerKa, double MaxMPerKa, double MedMPerKa);

public sealed record SegmentResult(
    string SegmentId, double TopM, double BottomM, string Status,
    CandidatePath[] Candidates, BandPoint[] Band, RateInterval[] Rates,
    Diagnostic[] Diagnostics, string ResultHash, int RejectedPathCount);

public sealed record ModelResult(
    ResultStatus Status, SegmentResult[] Segments, Diagnostic[] Diagnostics,
    string InputDigest, int ParamVersion)
{
    public static ModelResult Failure(ResultStatus status, IReadOnlyList<Diagnostic> diags, string digest, int paramVersion) =>
        new(status, Array.Empty<SegmentResult>(), diags.ToArray(), digest, paramVersion);
}
