namespace StratAge.Core.Domain;

public enum EventType
{
    ImportBatch,          // a batch of immutable material imported (baseline or delta)
    RetractMaterial,     // source data change: retract one direct source item
    ParameterRevision,   // rule set parameters changed
    Exclude,             // manual exclusion decision
    Reinstate,           // undo an exclusion
    TieBreak,            // order anchors at equal depth / choose ordering
    MarkInversion,       // accept a section as structurally inverted
    Freeze,              // freeze a candidate model
    Accept,              // accept a candidate / new anchor impact
    Reject,              // reject
    Branch,              // open a review branch
    Snapshot             // recorded review checkpoint (display values + deps)
}

/// <summary>
/// A mutable-layer decision/event. Immutable once recorded; payload references material
/// ids and carries the rule version and parent dependency so historical states can be
/// reconstructed without recomputing with the newest rules.
/// </summary>
public sealed record DecisionEvent(
    long Seq,
    string Id,
    string BranchId,
    EventType Type,
    string Summary,
    string RuleVersionId,
    IReadOnlyList<string> ParentEventIds,
    string InputDigest,          // digest of the material+parameter input this event acted on
    string PayloadJson,          // type-specific structured payload
    string Actor,
    DateTimeOffset At,
    bool Committed = true)
{
    public const string TrunkBranch = "trunk";
}

public sealed record Branch(string Id, string Name, long FromSeq, string Reason, DateTimeOffset At);

/// <summary>A single named, display-stable value captured in a snapshot for history replay.</summary>
public sealed record DisplayValue(string StableId, string Label, double Value, string Unit,
    double? Low, double? High, string Status);
