using System.Text.Json.Serialization;

namespace AgeModel.Core.Materials;

public static class EventTypes
{
    public const string ProjectCreated = "project.created";
    public const string MaterialsImported = "materials.imported";
    public const string SectionAdded = "section.added";
    public const string BoundaryAdded = "boundary.added";
    public const string BoundaryRevised = "boundary.revised";
    public const string OffsetAdded = "offset.added";
    public const string OffsetRevised = "offset.revised";
    public const string EventLayerAdded = "event-layer.added";
    public const string AnchorCreated = "anchor.created";
    public const string AnchorRevised = "anchor.revised";
    public const string ExclusionRecorded = "exclusion.recorded";
    public const string ExclusionReverted = "exclusion.reverted";
    public const string DecisionMade = "decision.made";
    public const string ParamsCreated = "params.created";
    public const string ParamsActivated = "params.activated";
    public const string ModelDerived = "model.derived";
    public const string ModelFrozen = "model.frozen";
    public const string FreezeRejected = "freeze.rejected";
    public const string ArchiveBaseline = "archive.baseline";
    public const string ArchiveProbe = "archive.probe";
}

/// <summary>An immutable, hash-chained event. The append-only spine of the whole system.</summary>
public sealed record ProjectEvent
{
    public required string EventId { get; init; }
    public required long Seq { get; init; }
    public required string Type { get; init; }
    /// <summary>Stable subject identity (material id / anchor id / model id ...).</summary>
    public required string SubjectId { get; init; }
    public required string PayloadJson { get; init; }
    /// <summary>Deterministic wall-time label (the seq remains the ordering truth).</summary>
    public required string OccurredAt { get; init; }
    public required string Actor { get; init; }
    /// <summary>Hash of previous event in the chain (empty for the first).</summary>
    public required string PrevHash { get; init; }
    /// <summary>SHA-256 over (EventId, Seq, Type, SubjectId, PayloadJson, OccurredAt, Actor, PrevHash).</summary>
    public required string Hash { get; init; }
    /// <summary>Input digest this event was based on (raw package digest / source version digest).</summary>
    public string? InputDigest { get; init; }
}
