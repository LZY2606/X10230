namespace ChronoCore;

public static class RuleSet
{
    public const string Version = "chrono-rules-1.0.0";
}

/// <summary>Versioned derivation parameters. A revision produces a new version; never mutates in place.</summary>
public sealed class ParameterSet
{
    public int Version { get; set; } = 1;
    public int MonteCarloDraws { get; set; } = 4000;
    public double OverlapTolerance { get; set; } = 0.02;        // P(older|deeper) below this => contradictory
    public int CandidateBudget { get; set; } = 256;             // resource budget for enumeration
    public long CpuBudgetMs { get; set; } = 2000;
    public double GridStepMetres { get; set; } = 0.05;
    public int RandomSeed { get; set; } = 20260923;

    public ParameterSet Clone() => (ParameterSet)MemberwiseClone();
}
