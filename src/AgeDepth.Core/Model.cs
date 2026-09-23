namespace AgeDepth.Core;

/// <summary>One continuous (unfaulted, unbroken) depth interval between explicit boundaries.</summary>
public sealed record Segment(
    string Id, string CoreId, double TopDepth, double BottomDepth,
    string? TopBoundaryId, string? BottomBoundaryId, IReadOnlyList<AgeAnchor> Anchors);

public sealed record RateInterval(
    string Id, string SegmentId, double TopDepth, double BottomDepth,
    double MedianRate, double P25, double P975, string Unit); // m/ka

public sealed record SegmentResult(
    string ResultId, Segment Segment, string Digest, bool Reused,
    double[] GridDepths, double[] Median, double[] P25, double[] P975,
    IReadOnlyList<RateInterval> Rates, IReadOnlyList<Diagnostic> Diagnostics);

public sealed record CandidateResult(
    string CandidateId, string Kind, string ParamVersion,
    IReadOnlyList<SegmentResult> Segments, IReadOnlyList<Diagnostic> Diagnostics);

public sealed record ModelRunResult(
    string RunId, string InputDigest, string ParamVersion, string? ParentRunId,
    IReadOnlyList<CandidateResult> Candidates, IReadOnlyList<Diagnostic> Diagnostics);

public sealed record ModelInput(
    IReadOnlyList<Section> Sections,
    IReadOnlyList<Boundary> Boundaries,
    IReadOnlyList<AgeAnchor> Anchors,
    IReadOnlyList<EventLayer> Events,
    ModelParameters Parameters);
