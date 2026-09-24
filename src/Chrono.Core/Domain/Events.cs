using System.Text.Json.Serialization;

namespace Chrono.Core.Domain;

/// <summary>Machine-readable judgment codes. The tricky stratigraphic rules are
/// emitted as structured events, not prose logs.</summary>
public static class JudgmentCode
{
    public const string MonotonicWithinUnbroken = "AGE_MONOTONIC_WITHIN_UNBROKEN_SECTION";
    public const string BarrierStopsMonotonic = "GAP_UNCONFORMITY_IS_EXPLICIT_BOUNDARY";
    public const string DistributionKeptNotMean = "ANCHOR_DISTRIBUTION_KEPT_NOT_MEAN";
    public const string TiedAnchorsKeepMultiple = "TIED_ANCHORS_KEEP_ALL_EQUALLY_OPTIMAL";
    public const string DepthDuplicate = "DEPTH_DUPLICATE_DETECTED";
    public const string InversionDetected = "INVERTED_SECTION_DETECTED";
    public const string LongTailDetected = "AGE_DISTRIBUTION_LONG_TAIL";
    public const string LocalRecompute = "LOCAL_RECOMPUTE_SCOPED";
    public const string FreezeConflict = "CONCURRENT_FROZEN_REJECTED";
    public const string ExclusionRetained = "EXCLUSION_DECISION_RETAINED";
    public const string UnconvertibleRetained = "UNCONVERTIBLE_VALUE_RETAINED";
}

public enum EventType
{
    ProjectCreated, ParameterRevision, SourceRevision, ImportBatch,
    AnchorAdded, AnchorRevised, AnchorExcluded, AnchorRestored,
    SegmentAdded, ReservoirSet, EventCorrelated, DecisionConflict,
    BranchCreated, ModelFrozen, ModelDerived, Judgment, ProbeRecorded
}

/// <summary>Immutable event envelope. Sequence + BranchId + ParentEventId give a
/// stable, replayable provenance graph. Payload JSON keeps the source fragment.</summary>
public sealed record StoredEvent
{
    public required string EventId { get; init; }
    public required long Seq { get; init; }
    public required string BranchId { get; init; }
    public required string? ParentEventId { get; init; }
    public required string RuleVersion { get; init; }
    public required string InputDigest { get; init; }
    public required EventType Type { get; init; }
    public required string PayloadJson { get; init; }
    public required string SourceFragment { get; init; }
    public required string Author { get; init; }
    public required string OccurredAt { get; init; }
}

/// <summary>A structured judgment attached to an event.</summary>
public sealed record Judgment(
    string Code,
    bool Holds,
    string Statement,
    IReadOnlyList<string> AffectedObjectIds,
    string? EvidenceEventId,
    IReadOnlyDictionary<string, string> Facts)
{
    public static Judgment Create(string code, bool holds, string statement,
        IReadOnlyList<string> affected, string? evidence = null,
        IReadOnlyDictionary<string, string>? facts = null) =>
        new(code, holds, statement, affected, evidence, facts ?? new Dictionary<string, string>());
}

public sealed record ParameterSet(
    string Version,
    double MaxTieDepthToleranceM,
    double MinRateCmPerKyr,
    double MaxRateCmPerKyr,
    double ReservoirDefaultSdYr,
    int CandidateBudget,
    IReadOnlyDictionary<string, string> Raw)
{
    public const string Current = "rules-2026.1";
}
