using System;

namespace Chrono.Core.Model;

/// <summary>Versioned modeling parameters. Revisions produce a new version and full re-derivation.</summary>
public sealed record RuleSet(
    string Version,
    string Interpolation,
    int DrawCount,
    ulong RngSeed,
    double LongTailProbabilityThreshold,
    int MaxCandidates,
    double MinSedRateCmPerYear,
    double MaxSedRateCmPerYear)
{
    public const string V1 = "rules/v1";
    public const string V2 = "rules/v2";

    public static RuleSet Default => new(V1, "piecewise_linear", 61, 0x5EEDUL, 0.05, 64, 1.0e-4, 2.0);

    /// <summary>
    /// v2: more draws (tighter Monte Carlo) and a wider allowed accumulation-rate envelope.
    /// This is the one scheduled parameter revision in the narrative case.
    /// </summary>
    public static RuleSet Revised => new(V2, "piecewise_linear", 127, 0xC0FFEEUL, 0.05, 64, 1.0e-4, 5.0);
}

/// <summary>The three distinct failure classes required by the spec.</summary>
public enum DerivationFailure
{
    None = 0,
    /// <summary>Resource budget exhausted (candidate enumeration cap hit) — distinct from input errors.</summary>
    BudgetExhausted = 1,
    /// <summary>The input bytes/structure cannot be parsed.</summary>
    FormatCorrupt = 2,
    /// <summary>Input parses but has no legal geological solution (e.g. zero-feasible inversion).</summary>
    SemanticNoSolution = 3
}
