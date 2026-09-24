namespace ChronoScale.Core;

/// <summary>Versioned rule set. Changing rules changes this so history can be re-derived per version.</summary>
public static class RuleVersions
{
    public const string Current = "chronoscale-rules-1.0.0";

    /// <summary>Tunable parameters (a parameter revision bumps the parameter version, not the rule version).</summary>
    public sealed record Parameters(
        long Version,
        int MaxMonteCarloSamples,
        int MaxCandidates,
        double MinimumHiatusYears,
        int EmpiricalTrimLimit,
        string? Note = null)
    {
        public static Parameters V1 => new(1, 4000, 24, 500, 4000, "baseline parameters");

        public Parameters With(long version, int? samples = null, int? candidates = null,
            double? hiatus = null, int? trim = null, string? note = null) =>
            new(version, samples ?? MaxMonteCarloSamples, candidates ?? MaxCandidates,
                hiatus ?? MinimumHiatusYears, trim ?? EmpiricalTrimLimit, note ?? Note);
    }
}
