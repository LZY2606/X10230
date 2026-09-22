using Chrono.Core.Model;

namespace Chrono.Core.Engine;

/// <summary>An anchor prepared for derivation: reservoir correction is derived, raw distribution is retained.</summary>
public sealed record PreparedAnchor(
    Anchor Anchor,
    AgeDistribution Calibrated,
    AgeDistribution Raw,
    double DepthMetres,
    string? ReservoirNote);

public sealed record AgeBandPoint(double DepthMetres, double Q05, double Median, double Q95);
public sealed record RateBandPoint(double DepthTopMetres, double DepthBottomMetres, double Q05, double Median, double Q95);

/// <summary>A defensible candidate depth-age model for one continuous run.</summary>
public sealed record CandidateModel(
    string Id,
    string RunId,
    string Strategy,
    string TieKey,
    double Score,
    IReadOnlyList<AgeBandPoint> AgeBand,
    IReadOnlyList<RateBandPoint> RateBand,
    IReadOnlyList<string> AnchorIdsOrdered,
    bool EqualOptimal,
    string InputSummary,
    int ParametersVersion,
    int MaterialsVersion);

public sealed record RunModel(
    string Id,
    double TopMetres,
    double BottomMetres,
    bool BoundedByGap,
    IReadOnlyList<string> BoundaryEventIds,
    IReadOnlyList<PreparedAnchor> Anchors,
    IReadOnlyList<CandidateModel> Candidates,
    IReadOnlyList<CandidateModel> EqualOptimalCandidates,
    IReadOnlyList<Diagnostic> Diagnostics,
    bool SemanticBlocked,
    string? BlockReason);

public sealed record DeriveReport(
    string Id,
    string ProjectId,
    string BranchId,
    string? AsOfEventId,
    string RuleVersion,
    int ParametersVersion,
    int MaterialsVersion,
    string InputSummary,
    IReadOnlyList<string> ParentDependencyIds,
    IReadOnlyList<Diagnostic> Diagnostics,
    IReadOnlyList<RunModel> Runs,
    IReadOnlyList<Model.RuleFindingView> Findings,
    FailureKind? Failure,
    long DrawsUsed);
