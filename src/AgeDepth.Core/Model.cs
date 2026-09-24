using System.Text.Json.Serialization;

namespace AgeDepth.Core;

/// <summary>
/// Versioned derivation parameters. A result records its rule version and parameter digest
/// so a checkout can reproduce exactly the earlier visible conclusion.
/// </summary>
public sealed record RuleSet(
    string Version,
    bool ApplyReservoir,
    bool ConstrainMonotonicWithinSections,
    int MonteCarloSamples,
    long MaxComputeTicks,
    double TieToleranceDepthM,
    double TopToleranceYr,
    int MaxCandidates)
{
    public const string CurrentVersion = "rules-2026.09.1";

    public static RuleSet Default => new(
        CurrentVersion, true, true, 2000, TimeSpan.FromSeconds(20).Ticks,
        0.002, 1.0, 12);

    public string ParameterDigest =>
        Determinism.Sha8(string.Join("|",
            Version, ApplyReservoir, ConstrainMonotonicWithinSections,
            MonteCarloSamples, TieToleranceDepthM, TopToleranceYr, MaxCandidates));
}

/// <summary>The boundary situations the counterexample archive must actually traverse.</summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum Situation
{
    DepthDuplicate,
    Hiatus,
    Unconformity,
    InvertedSection,
    TiedAnchors,
    LongTailDistribution,
    LocalRecompute,
    ConcurrentFreeze,
    MonotonicViolation
}

/// <summary>A point on an age-depth curve with the displayed units and its stable identity.</summary>
public sealed record CurvePoint(
    string Id,
    double DepthM,
    double AgeYr,
    double? AgeP05,
    double? AgeP95,
    string SectionId);

/// <summary>Sedimentation rate interval per continuous section.</summary>
public sealed record RateInterval(
    string SectionId,
    double RateMedianCmKa,
    double RateP05CmKa,
    double RateP95CmKa,
    double FromDepthM,
    double ToDepthM);

/// <summary>
/// One defensible depth-age candidate. Equally-good (within <see cref="RuleSet.TopToleranceYr"/>)
/// candidates are all retained and presented as a lineage, never collapsed to a winner.
/// </summary>
public sealed record ModelCandidate(
    string Id,
    string Lineage,
    string Method,
    double FitScore,
    double Roughness,
    bool IsTiedBest,
    IReadOnlyList<CurvePoint> Curve,
    IReadOnlyList<RateInterval> Rates,
    IReadOnlyList<string> UsedAnchorIds,
    string InputDigest,
    string RuleDigest,
    string? ParentCandidateId);

/// <summary>Outcome of a derivation over a checkout of the decision event log.</summary>
public sealed record ModelResult(
    EngineStatus Status,
    string RuleVersion,
    string RuleDigest,
    string InputDigest,
    IReadOnlyList<Diag> Diagnostics,
    IReadOnlyList<ModelCandidate> Candidates,
    IReadOnlyList<Situation> Situations,
    IReadOnlyList<string> FrozenCandidateIds,
    long SamplesDrawn,
    bool BudgetExhausted);

/// <summary>Report of which objects were recomputed when one source value changed.</summary>
public sealed record RecomputeReport(
    string ChangedSourcePath,
    IReadOnlyList<string> RecomputedCandidateIds,
    IReadOnlyList<string> UnaffectedCandidateIds,
    IReadOnlyList<string> InvalidatedDecisionIds);
