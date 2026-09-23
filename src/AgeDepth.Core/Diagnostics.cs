namespace AgeDepth.Core;

/// <summary>
/// The three failure outcomes are distinct results, never collapsed into one
/// another and never masked by empty/default values.
/// </summary>
public enum FailureKind
{
    BudgetExhausted,
    FormatCorrupt,
    SemanticallyUnsolvable
}

/// <summary>A diagnostic always carries a position (material id and/or depth).</summary>
public sealed record Diagnostic(
    string Code,
    string Message,
    string? TargetId,
    double? DepthMetres,
    string? Position);

public abstract record EngineOutcome
{
    private EngineOutcome() { }

    public sealed record Success(AgeDepthModel Model) : EngineOutcome;

    public sealed record Failed(FailureKind Kind, IReadOnlyList<Diagnostic> Diagnostics) : EngineOutcome;
}

/// <summary>
/// Machine-readable judgement: why a rule fired, on what subject, and on
/// which basis. These are emitted as structured state (and persisted as
/// events), not as log lines.
/// </summary>
public sealed record Judgment(
    string Rule,
    string Subject,
    string Decision,
    string Basis);

public static class Rules
{
    public const string MonotonicOnlyWithinUnbrokenSegment = "monotonic-only-within-unbroken-segment";
    public const string DistributionNotReplacedByMean = "anchor-distribution-not-replaced-by-mean";
    public const string EquiprobableCandidatesRetained = "equiprobable-candidates-retained";
    public const string GapIsExplicitBoundary = "depth-gap-is-explicit-boundary";
    public const string UnconformityIsExplicitBoundary = "unconformity-is-explicit-boundary";
}
