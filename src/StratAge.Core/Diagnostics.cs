namespace StratAge.Core;

/// <summary>三种必须可区分的失败终态：资源预算耗尽、格式损坏、语义无解。</summary>
public enum OutcomeKind
{
    Ok = 0,
    ResourceBudgetExhausted = 1,
    FormatCorrupt = 2,
    SemanticallyUnsolvable = 3,
    ConflictFrozen = 4,
    ConflictSequence = 5,
}

public sealed record Diag(
    string Code,
    string Severity,
    string Message,
    string? JsonPointer = null,
    string? StableObjectId = null)
{
    public const string IncompleteInput = "INCOMPLETE_INPUT";
    public const string FormatCorrupt = "FORMAT_CORRUPT";
    public const string UnitUnknown = "UNIT_UNKNOWN";
    public const string Unconvertible = "UNCONVERTIBLE_VALUE";
    public const string DuplicateDepth = "DUPLICATE_DEPTH";
    public const string TiedDepth = "TIED_DEPTH";
    public const string InvertedOrder = "INVERTED_ORDER";
    public const string MonotonicityBroken = "MONOTONICITY_BROKEN";
    public const string GapBoundary = "GAP_BOUNDARY";
    public const string DistributionLongTail = "DISTRIBUTION_LONG_TAIL";
    public const string ReservoirUnresolved = "RESERVOIR_OFFSET_UNRESOLVED";
    public const string ExcludedObject = "OBJECT_EXCLUDED";
    public const string BudgetExhausted = "RESOURCE_BUDGET_EXHAUSTED";
    public const string NoLegalCandidate = "NO_LEGAL_CANDIDATE";
    public const string EventAmbiguous = "EVENT_CORRELATION_AMBIGUOUS";

    public bool IsBlocking => Severity == "error";
}
