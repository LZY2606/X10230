namespace Chrono.Core;

/// <summary>
/// Machine-readable judgments. These are the audit facts the counterexample archive and replay
/// reports are built from — structured state, not prose logs.
/// </summary>
public enum FindingKind
{
    DepthDuplicate,
    UnconformityBoundary,
    GapBoundary,
    AgeInversion,
    CoLocatedTie,
    LongTailDistribution,
    LocalRecompute,
    ConcurrentFreeze,
    MonotonicWithinSection,
    DistributionKeptNotMean,
    MultipleOptimaRetained,
    IncompleteInput,
    FormatCorrupt,
    SemanticUnsolvable,
    BudgetExhausted,
    ScopeContained,
    RollbackRestored,
    IdentityStable
}

public sealed record Finding(
    FindingKind Kind,
    string Statement,
    IReadOnlyList<string> AffectedIds,
    IReadOnlyDictionary<string, string> Evidence);
