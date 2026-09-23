namespace CoreChronology;

public enum ScoringMethod { RateCoefficientOfVariation, LeaveOneOutZScore }
public enum ReservoirPolicy { ApplyAllDeclared, IgnoreReservoir }

/// <summary>Versioned model-building parameters. Each revision is a new version.</summary>
public sealed record ModelParameters(
    int Version,
    ScoringMethod Scoring,
    ReservoirPolicy Reservoir,
    int MonteCarloDraws,
    int MaxCandidates,
    double GridStepM,
    double[] Quantiles)
{
    public static readonly ModelParameters V1 = new(
        Version: 1,
        Scoring: ScoringMethod.RateCoefficientOfVariation,
        Reservoir: ReservoirPolicy.ApplyAllDeclared,
        MonteCarloDraws: 4000,
        MaxCandidates: 64,
        GridStepM: 0.05,
        Quantiles: new[] { 0.05, 0.5, 0.95 });

    public static readonly ModelParameters V2 = V1 with
    {
        Version = 2,
        Scoring = ScoringMethod.LeaveOneOutZScore,
        MonteCarloDraws = 6000,
    };
}
