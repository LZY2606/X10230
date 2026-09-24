namespace AgeDepth.Engine;

public sealed record RateInterval
{
    public required double DepthTop { get; init; }
    public required double DepthBottom { get; init; }
    public required double RateP025 { get; init; }
    public required double RateP500 { get; init; }
    public required double RateP975 { get; init; }
}

public sealed record BandNode
{
    public required double Depth { get; init; }
    public required double AgeP025 { get; init; }
    public required double AgeP500 { get; init; }
    public required double AgeP975 { get; init; }
    public required IReadOnlyList<string> AnchorIds { get; init; }
}

/// <summary>One sampled, monotonic age-depth realisation kept as a defensible candidate.</summary>
public sealed record CandidateModel
{
    public required string CandidateId { get; init; }
    public required int DrawIndex { get; init; }
    public required double Roughness { get; init; }
    public required bool CoOptimal { get; init; }
    public required IReadOnlyList<(double Depth, double Age)> Nodes { get; init; }
    public required IReadOnlyList<double> Rates { get; init; }
}

public sealed record SegmentModel
{
    public required string SegmentId { get; init; }
    public bool Solved { get; init; }
    public IReadOnlyList<BandNode> Band { get; init; } = Array.Empty<BandNode>();
    public IReadOnlyList<RateInterval> Rates { get; init; } = Array.Empty<RateInterval>();
    public IReadOnlyList<CandidateModel> Candidates { get; init; } = Array.Empty<CandidateModel>();
    public IReadOnlyList<CandidateModel> CoOptimalCandidates { get; init; } = Array.Empty<CandidateModel>();
    /// <summary>Stable identity of this segment result: same inputs + parameters => same hash.</summary>
    public required string InputHash { get; init; }
}

public sealed record ModelResult
{
    public required ComputeStatus Status { get; init; }
    public required string ParametersVersion { get; init; }
    public required string InputHash { get; init; }
    public IReadOnlyList<Diagnostic> Diagnostics { get; init; } = Array.Empty<Diagnostic>();
    public IReadOnlyList<SegmentModel> Segments { get; init; } = Array.Empty<SegmentModel>();
    public int AcceptedDraws { get; init; }
    public int AttemptedDraws { get; init; }
}
