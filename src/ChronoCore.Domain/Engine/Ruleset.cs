namespace ChronoCore.Domain.Engine;

/// <summary>Versioned parameters. Every derived result records the exact
/// ruleset version plus an input digest, so any historical event can be
/// reconstructed instead of overwritten by the newest computation.</summary>
public sealed record Ruleset(
    string Version,
    bool EnforceMonotonicWithinSection,
    bool TreatTieAsEquivalent,
    InterpolationKind Interpolation,
    int MonteCarloDraws,
    int GridPointsPerSection,
    uint RandomSeed,
    double EventTieWeight,
    double ParsimonyWeight)
{
    public static readonly Ruleset V1 = new(
        "rules-1.0",
        EnforceMonotonicWithinSection: true,
        TreatTieAsEquivalent: true,
        Interpolation: InterpolationKind.Pchip,
        MonteCarloDraws: 400,
        GridPointsPerSection: 40,
        RandomSeed: 0xC0_7E_5EED,
        EventTieWeight: 1.0,
        ParsimonyWeight: 0.05);

    public string Fingerprint =>
        $"{Version}|M={EnforceMonotonicWithinSection}|T={TreatTieAsEquivalent}|I={Interpolation}|" +
        $"N={MonteCarloDraws}|G={GridPointsPerSection}|S={RandomSeed}|W={EventTieWeight},{ParsimonyWeight}";
}

public enum InterpolationKind
{
    Linear = 0,
    Pchip = 1
}
