namespace AgeDepth.Core;

/// <summary>Immutable material: a continuous cored section. Depths carry a datum.</summary>
public sealed record Section(
    string Id, string CoreId, double TopDepth, double BottomDepth, string Datum, string Lithology);

/// <summary>Immutable material: explicit boundary (hiatus / unconformity / gap). Never implicit.</summary>
public sealed record Boundary(
    string Id, double Depth, string Kind, string Datum, string Note);

/// <summary>
/// Immutable material: an age anchor. The full distribution is preserved;
/// it is never collapsed to a mean. Reservoir offset is kept with its unit.
/// </summary>
public sealed record AgeAnchor(
    string Id, double Depth, string Datum, AgeDistribution Distribution,
    double ReservoirOffsetYears, string Material, string Lab, string? Supersedes = null);

public sealed record AgeDistribution(
    string Kind,           // point | normal | uniform | longtail | empirical
    double? Mean, double? Sd,
    double? Min, double? Max,
    double? Mu, double? Sigma,
    double[]? Samples,
    string Unit);          // "yr BP" | "ka BP"

/// <summary>Immutable material: an event layer correlated across cores.</summary>
public sealed record EventLayer(
    string Id, double Depth, string Datum, string EventKind,
    double? CorrelationConfidence, string Note);

public sealed record ModelParameters(
    string ParamVersion, int Iterations, int MaxIterations, double GridStep);

public static class CandidateKinds
{
    public const string PiecewiseLinear = "piecewise-linear";
    public const string ConstantRate = "constant-rate";
    public const string Smoothed = "smoothed";
    public static readonly string[] All = { PiecewiseLinear, ConstantRate, Smoothed };
}
