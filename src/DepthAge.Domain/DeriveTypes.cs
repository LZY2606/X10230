namespace DepthAge.Domain;

/// <summary>
/// The three hard failure modes are distinct results, never collapsed:
/// resource budget exhausted, semantic no-solution, and (at import level) format corrupt.
/// </summary>
public enum DeriveStatus { Ok, BudgetExhausted, SemanticNoSolution }

public sealed record DeriveOptions
{
    /// <summary>Optional cap on total (candidate combos x realizations). Null = unlimited.</summary>
    public int? RealizationBudget { get; init; }
}

public sealed record SpanRate
{
    public required double FromDepth { get; init; }
    public required double ToDepth { get; init; }
    public required double Median { get; init; }
    public required double Low { get; init; }
    public required double High { get; init; }
    public required string Unit { get; init; }
}

public sealed record BandPoint
{
    public required double Depth { get; init; }
    public required double Median { get; init; }
    public required double Low { get; init; }
    public required double High { get; init; }

    /// <summary>False in extrapolated reaches (above shallowest / below deepest anchor).</summary>
    public required bool Constrained { get; init; }
}

/// <summary>
/// One defensible depth-age model candidate. All candidates that tie within tolerance of
/// the best score are kept (Accepted = true); none is silently collapsed into a mean model.
/// </summary>
public sealed record CandidateModel
{
    public required string CandidateKey { get; init; }
    public required string GroupKey { get; init; }
    public required InterpolatorKind Interpolator { get; init; }
    public required string[] AnchorIds { get; init; }
    public required double Score { get; init; }
    public int Rank { get; set; }
    public bool Accepted { get; set; }
    public string? RejectedReason { get; init; }
    public required double MonotonicFraction { get; init; }
    public required int Realizations { get; init; }
    public required BandPoint[] Band { get; init; }
    public required SpanRate[] Rates { get; init; }
}

public sealed record GroupResult
{
    public required string GroupKey { get; init; }
    public required string GroupDigest { get; init; }
    public required string[] SegmentIds { get; init; }
    public required double TopDepth { get; init; }
    public required double BottomDepth { get; init; }
    public required DeriveStatus Status { get; init; }
    public required string[] AnchorIds { get; init; }
    public required CandidateModel[] Candidates { get; init; }
    public required Diagnostic[] Diagnostics { get; init; }
}

/// <summary>
/// Content-addressed derivation result. Records input digest, rule-set version and parent
/// dependencies (per-group input digests). Local recompute evidence: which groups were
/// reused unchanged vs recomputed.
/// </summary>
public sealed record DerivationRecord
{
    public required string DerivationId { get; init; }
    public required string RuleSetVersion { get; init; }
    public required string InputsDigest { get; init; }
    public required DeriveStatus Status { get; init; }
    public required GroupResult[] Groups { get; init; }
    public required Diagnostic[] Diagnostics { get; init; }
    public required string[] ParentDependencies { get; init; }
    public required string[] ReusedGroupDigests { get; init; }
    public required string[] RecomputedGroupDigests { get; init; }
    public required string CreatedBy { get; init; }
    public required string CreatedAt { get; init; }
}
