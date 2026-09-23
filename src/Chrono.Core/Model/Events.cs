namespace Chrono.Core.Model;

public enum DecisionType
{
    ParameterRevision,
    SourceDataChange,
    ManualAccept,
    ManualReject,
    ParameterAdjustment,
    Freeze,
    Unfreeze
}

/// <summary>
/// Machine-readable event. Events are append-only; replaying the event stream against
/// an immutable materials version restores any historical state.
/// </summary>
public sealed record ChronoEvent(
    string Id,
    int Seq,
    string ProjectId,
    string BranchId,
    int? ParentEventSeq,
    DecisionType Type,
    string Summary,
    string MaterialsVersion,
    string RuleSetVersion,
    string InputDigest,
    IReadOnlyDictionary<string, string> Payload,
    string Actor,
    string CreatedAt)
{
    /// <summary>Content identity of the event (independent of database row id).</summary>
    public string ContentId { get; init; } = "";
}

/// <summary>Versioned business parameters; the rules themselves are versioned in code.</summary>
public sealed record RuleSet(
    string Version,
    int MonteCarloDraws,
    long CpuTimeBudgetMs,
    double LongTailThreshold,
    double ParallelScoreTolerance,
    uint RandomSeed,
    bool RequireStrictMonotonicity)
{
    public static RuleSet V1 => new("rules-2026-09-v1", 4000, 2000, 0.10, 0.05, 20260919u, true);
    public static RuleSet V2 => V1 with { Version = "rules-2026-09-v2", MonteCarloDraws = 6000, LongTailThreshold = 0.08, RequireStrictMonotonicity = true };
}
