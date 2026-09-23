namespace Chrono.Core.Model;

/// <summary>One defensible candidate depth-age model. Multiple equally-good candidates are all retained.</summary>
public sealed record CandidateModel(
    string CandidateId,
    string RankKey,
    bool Accepted,
    double Score,
    bool Tie,
    IReadOnlyList<PointAge> Knots,
    IReadOnlyList<RateBand> RateBands,
    IReadOnlyList<UncertaintyBand> UncertaintyBands,
    IReadOnlyList<string> UsedAnchorIds,
    IReadOnlyList<Diagnostic> Violations,
    string InputDigest,
    string RuleSetVersion,
    string MaterialsVersion)
{
    public bool IsLegal => !Violations.Any(v => v.Severity == Severity.Error);
}

public sealed record PointAge(string AnchorId, double DepthM, double MedianCalBp, double P05CalBp, double P95CalBp, string DistributionKind);

public sealed record RateBand(string SegmentId, double DepthTopM, double DepthBottomM, double MedianCmPerKyr, double P05CmPerKyr, double P95CmPerKyr, Facing Facing);

public sealed record UncertaintyBand(double DepthTopM, double DepthBottomM, double P05CalBp, double P95CalBp, string SegmentId);

/// <summary>Result of deriving candidates from one materials version + event decision state.</summary>
public sealed record DerivationResult(
    string DerivationId,
    string MaterialsVersion,
    string RuleSetVersion,
    string InputDigest,
    IReadOnlyList<CandidateModel> Candidates,
    IReadOnlyList<StructuredState> States,
    IReadOnlyList<Diagnostic> Diagnostics,
    bool BudgetExhausted)
{
    public bool Success => !BudgetExhausted;
}

/// <summary>Structured replayable state for one boundary-condition situation.</summary>
public sealed record StructuredState(
    string StateId,
    string Situation,
    string Status,
    IReadOnlyList<string> AffectedObjectIds,
    string? InputFragment,
    IReadOnlyDictionary<string, string> Evidence)
{
    public bool IsAdverse => Status is "violation" or "excluded" or "unresolved";
}
