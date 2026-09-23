namespace GeoChron.Core;

public sealed record ModelParameters(
    int Version,
    int Iterations,
    int MaxCandidates,
    double TieToleranceM,
    double OverlapToleranceSd,
    int Seed)
{
    public static ModelParameters Default => new(1, 400, 24, 0.02, 1.5, 20260919);
}

/// <summary>Uncertainty envelope node: age quantiles at one depth, unit yrBP.</summary>
public sealed record DepthNode(double DepthM, double P5, double P50, double P95);

/// <summary>Sedimentation-rate interval between two neighbouring anchors, m/kyr.</summary>
public sealed record RateInterval(
    string SegmentId,
    double TopM,
    double BottomM,
    double RateP5,
    double RateP50,
    double RateP95,
    string Unit);

public static class ModelStatus
{
    public const string Candidate = "candidate";
    public const string Frozen = "frozen";
    public const string Adopted = "adopted";
    public const string Rejected = "rejected";
    public const string Superseded = "superseded";
}

/// <summary>
/// One defensible depth-age model candidate for one continuous segment.
/// Identity is a content hash: equivalent inputs yield the same id and order.
/// </summary>
public sealed record ModelCandidate(
    string Id,
    string SegmentId,
    string[] AnchorIds,
    string[] DroppedAnchorIds,
    string InputDigest,
    int ParamVersion,
    string[] DependsOn,
    DepthNode[] Envelope,
    RateInterval[] Rates,
    int AcceptedIterations,
    int RejectedIterations,
    string Status,
    string Rationale);
