namespace Chrono.Core;

/// <summary>Versioned modelling parameters. A revision id travels with every derived result.</summary>
public sealed record ParameterSet
{
    public string VersionId { get; init; } = "rules-v1";
    public int MonteCarloDraws { get; init; } = 2000;
    public double BandGridStepM { get; init; } = 0.25;
    public double TieToleranceSigma { get; init; } = 0.25;
    public int CandidateBudget { get; init; } = 64;
    public string Interpolation { get; init; } = "linear";
    public string TimeDatum { get; init; } = "cal BP (before CE 1950)";
    public string DepthDatum { get; init; } = "mbsf, down-positive";

    public static ParameterSet V1() => new();
}
