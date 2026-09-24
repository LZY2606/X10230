namespace Chrono.Engine;

/// <summary>派生视图：深度年龄模型的全部可辩护候选与结构化判定状态。</summary>
public sealed class DepthAgeView
{
    public required string StateHash { get; init; }
    public required string ParamsVersion { get; init; }
    public required List<SegmentView> Segments { get; init; }
    public required List<RateInterval> RateIntervals { get; init; }
    public required List<UncertaintyBand> UncertaintyBands { get; init; }
    public required List<DiagnosticView> Diagnostics { get; init; }
    public required List<string> CandidateLineage { get; init; }
    public required string Outcome { get; init; }
}

public sealed class SegmentView
{
    public required string SegmentId { get; init; }
    public required string Label { get; init; }
    public double TopDepth { get; init; }
    public double BaseDepth { get; init; }
    public required string UpperBoundary { get; init; }
    public required string LowerBoundary { get; init; }
    public bool IsFaulted { get; init; }
    public bool HasDuplicateDepthConflict { get; init; }
    public bool HasInversion { get; init; }
    public bool InversionLegitimized { get; init; }
    public required List<string> AnchorIds { get; init; }
    public required List<CandidateView> Candidates { get; init; }
    public required List<DiagnosticView> Diagnostics { get; init; }
    public required List<string> States { get; init; }
}

public sealed class CandidateView
{
    public required string CandidateId { get; init; }
    public required string SegmentId { get; init; }
    /// <summary>该候选采用的锚点子集（并列锚点择其一）。</summary>
    public required List<string> ChosenAnchorIds { get; init; }
    /// <summary>被该候选排除的并列锚点（结果仍保留，不删除）。</summary>
    public required List<string> DeclinedAnchorIds { get; init; }
    public required double Score { get; init; }
    public required bool IsOptimal { get; init; }
    public required bool Monotonic { get; init; }
    public required double SlopeYearsPerMetre { get; init; }
    public required double SlopeLow { get; init; }
    public required double SlopeHigh { get; init; }
    public required double FitResidualMedian { get; init; }
    public required string InputDigest { get; init; }
    public required string ParamsVersion { get; init; }
    public required List<string> ParentDependsOn { get; init; }
    public bool Adopted { get; set; }
}

public sealed class RateInterval
{
    public required string RateId { get; init; }
    public required string SegmentId { get; init; }
    public required string? CandidateId { get; init; }
    public double FromDepth { get; init; }
    public double ToDepth { get; init; }
    public double RateMedian { get; init; }
    public double RateLow { get; init; }
    public double RateHigh { get; init; }
    public string Unit { get; init; } = "cal BP yr / m";
}

public sealed class UncertaintyBand
{
    public required string BandId { get; init; }
    public required string SegmentId { get; init; }
    public required string? CandidateId { get; init; }
    public double Depth { get; init; }
    public double AgeMedian { get; init; }
    public double AgeLow { get; init; }
    public double AgeHigh { get; init; }
    public required string TimeBasis { get; init; }
}

public sealed record DiagnosticView(string Code, string Severity, string Message,
    string? MaterialId, string? Field, string? Locator);
