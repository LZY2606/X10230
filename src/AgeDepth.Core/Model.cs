namespace AgeDepth.Core;

/// <summary>One node of a candidate age-depth curve. Depth in m (datum basis), age in yr BP.</summary>
public sealed record CurveNode(double DepthM, double AgeYrBp, string? AnchorId);

/// <summary>A single defensible candidate model. Equiprobable candidates share equal weight.</summary>
public sealed record CandidateModel(
    string CandidateId,
    double Weight,
    IReadOnlyList<CurveNode> Nodes);

public enum SegmentStatus { Resolved, Contested, Unsolvable }

/// <summary>Uncertainty band at one depth, derived from the candidate ensemble (never a mean).</summary>
public sealed record BandNode(double DepthM, double P5, double P50, double P95);

/// <summary>Sedimentation-rate interval between two adjacent nodes, in cm/kyr.</summary>
public sealed record RateInterval(
    string IntervalId,
    double FromDepthM,
    double ToDepthM,
    double MinCmPerKyr,
    double MaxCmPerKyr);

public sealed record SegmentModel(
    string SegmentId,
    string? SectionId,
    string InputHash,
    double TopM,
    double BottomM,
    SegmentStatus Status,
    IReadOnlyList<string> AnchorIds,
    IReadOnlyList<BandNode> Band,
    IReadOnlyList<RateInterval> Rates,
    IReadOnlyList<string> BoundaryIds);

public sealed record AgeDepthModel(
    string ModelId,
    string ParamsVersion,
    string InputHash,
    IReadOnlyList<SegmentModel> Segments,
    IReadOnlyList<CandidateModel> Candidates,
    IReadOnlyList<Judgment> Judgments,
    IReadOnlyList<Diagnostic> Diagnostics);
