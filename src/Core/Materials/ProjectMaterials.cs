using System.Text.Json.Serialization;
using AgeModel.Core.Distributions;
using AgeModel.Core.Quantities;

namespace AgeModel.Core.Materials;

public enum BoundaryKind { Conformable, Unconformity, Gap }
public enum AnchorStatus { Included, Excluded }
public enum DecisionKind { Include, Exclude, ParameterChange, ForceAccept, RejectConflict }
public enum DecisionResult { Applied, Rejected }

public sealed record CoreSection
{
    public required string Id { get; init; }
    public required string Name { get; init; }
    public required int OrderIndex { get; init; }
    public required RawQuantity TopDepth { get; init; }
    public required RawQuantity BottomDepth { get; init; }
    public string Lithology { get; init; } = "";
    public string Notes { get; init; } = "";
}

public sealed record Boundary
{
    public required string Id { get; init; }
    public required BoundaryKind Kind { get; init; }
    /// <summary>Section above (shallower) and below (deeper), null for outer frame edges.</summary>
    public string? AboveSectionId { get; init; }
    public string? BelowSectionId { get; init; }
    public required RawQuantity AtDepth { get; init; }
    /// <summary>Missing stratigraphic interval; explicit wall across which monotonicity does NOT apply.</summary>
    public RawQuantity? MissingInterval { get; init; }
    public string Name { get; init; } = "";
    public string Notes { get; init; } = "";
}

public sealed record ReservoirOffset
{
    public required string Id { get; init; }
    public required string Name { get; init; }
    /// <summary>Global reservoir age R (raw, e.g. "405" a).</summary>
    public required RawQuantity OffsetYears { get; init; }
    /// <summary>Local correction ΔR (raw, can be negative).</summary>
    public RawQuantity? DeltaR { get; init; }
    /// <summary>Datums/scope the offset applies to, e.g. reservoir name.</summary>
    public string Scope { get; init; } = "";
    public string Notes { get; init; } = "";
}

public sealed record EventLayer
{
    public required string Id { get; init; }
    public required string Name { get; init; }
    public required RawQuantity Depth { get; init; }
    /// <summary>Correlation key joining occurrences across sections/cores.</summary>
    public string CorrelationKey { get; init; } = "";
    /// <summary>Reference age distribution for the event (e.g. known tephra age).</summary>
    public AgeDistribution? ReferenceAge { get; init; }
    public string ReferenceDatum { get; init; } = "";
    public string Notes { get; init; } = "";
}

/// <summary>An immutable version of an age anchor: raw depth, raw distribution text and links are preserved.</summary>
public sealed record AnchorVersion
{
    public required string VersionId { get; init; }
    public required RawQuantity Depth { get; init; }
    /// <summary>Verbatim distribution specification as imported (never normalized away).</summary>
    public required string DistributionRaw { get; init; }
    /// <summary>Parsed distribution in canonical years BP; null when unconvertible (diagnostic retained).</summary>
    public AgeDistribution? Distribution { get; init; }
    public string? ReservoirOffsetId { get; init; }
    public string? EventLayerId { get; init; }
    public string MaterialSource { get; init; } = "";
    public string ChangeReason { get; init; } = "";
    public string IntroducedByEventId { get; init; } = "";
}

public sealed record Anchor
{
    public required string Id { get; init; }
    public string SectionId { get; init; } = "";
    public string Name { get; init; } = "";
    /// <summary>Stable across versions; equal to first VersionId at creation.</summary>
    public string CurrentVersionId { get; set; } = "";
    public List<AnchorVersion> Versions { get; init; } = new();
    public AnchorStatus Status { get; set; } = AnchorStatus.Included;
    /// <summary>Exclusion/decision record id currently governing status, if any.</summary>
    public string? ActiveDecisionId { get; set; }

    public AnchorVersion Current => Versions.First(v => v.VersionId == CurrentVersionId);
}

public sealed record ExclusionRecord
{
    public required string Id { get; init; }
    public required string SubjectKind { get; init; } // "anchor" | "event-layer" | "offset"
    public required string SubjectId { get; init; }
    public required string SubjectVersionId { get; init; }
    /// <summary>true = excluded from derivation; false = explicitly retained/included.</summary>
    public required bool Excluded { get; init; }
    public required string Reason { get; init; }
    public required string DecidedBy { get; init; } // "import-package" | "manual:<name>"
    public required string IntroducedByEventId { get; init; }
    public bool Reverted { get; init; }
    public string? RevertedByEventId { get; init; }
}

public sealed record ManualDecision
{
    public required string Id { get; init; }
    public required DecisionKind Kind { get; init; }
    public required string SubjectId { get; init; }
    public string? SubjectVersionId { get; init; }
    public string? ParameterSetId { get; init; }
    public string PayloadJson { get; init; } = "{}";
    public required DecisionResult Result { get; init; }
    public string Reason { get; init; } = "";
    public string? ConflictCode { get; init; }
    public required string EventId { get; init; }
    public required int EventSeq { get; init; }
}
