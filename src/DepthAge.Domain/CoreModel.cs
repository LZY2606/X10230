namespace DepthAge.Domain;

/// <summary>Depth datum / coordinate reference for a core. Immutable material.</summary>
public sealed record DatumRef
{
    /// <summary>Stable id, e.g. "datum-tc2024a".</summary>
    public required string Id { get; init; }

    /// <summary>Machine-readable datum definition, e.g. "core-top:0cm:down-positive".</summary>
    public required string Definition { get; init; }

    /// <summary>Canonical depth unit for normalized values.</summary>
    public required string Unit { get; init; }

    public required string Description { get; init; }
}

/// <summary>A logged core section. Immutable material. Depths in datum coordinates.</summary>
public sealed record Segment
{
    public required string Id { get; init; }
    public required string CoreId { get; init; }
    public required string Label { get; init; }
    public required Measured Top { get; init; }
    public required Measured Bottom { get; init; }
    public required string Lithology { get; init; }
    public required string SourceRef { get; init; }
}

public enum BoundaryKind { Unconformity, Hiatus, Fault, ConformableGap }

/// <summary>
/// An explicit stratigraphic boundary between two segments. Unconformities and gaps are
/// explicit breaks in the depth axis: monotonicity is only required within a continuous
/// run of segments, never across a boundary.
/// </summary>
public sealed record BoundarySpec
{
    public required string Id { get; init; }
    public required string AboveSegmentId { get; init; }
    public required string BelowSegmentId { get; init; }
    public required BoundaryKind Kind { get; init; }
    public required string Note { get; init; }
    public required string SourceRef { get; init; }
}

public enum AnchorKind { Radiocarbon, Tephra, Paleomag, Biostrat, Trial }

/// <summary>
/// An age anchor: a dated level in the core. The age is always a full distribution,
/// never a point estimate. Immutable material once imported.
/// </summary>
public sealed record Anchor
{
    public required string Id { get; init; }
    public required string CoreId { get; init; }
    public required AnchorKind Kind { get; init; }
    public required Measured Depth { get; init; }

    /// <summary>Age distribution as imported (radiocarbon anchors carry 14C yr BP).</summary>
    public required AgeDist Age { get; init; }

    /// <summary>Reservoir model id for radiocarbon anchors; null for calendar-age anchors.</summary>
    public string? ReservoirModelId { get; init; }

    /// <summary>Event-bed correlation id when this anchor realizes a correlated event bed.</summary>
    public string? CorrelationId { get; init; }

    public required string LabRef { get; init; }
    public required string SourceRef { get; init; }
}

/// <summary>An event bed observed in this core (tephra layer, flood layer, ...). Immutable material.</summary>
public sealed record EventBed
{
    public required string Id { get; init; }
    public required string CoreId { get; init; }
    public required string Label { get; init; }
    public required Measured Depth { get; init; }
    public required string SourceRef { get; init; }
}

/// <summary>
/// Correlation of an event bed to a regional event with a known age distribution.
/// Immutable material; supplies calendar ages to anchors that reference it.
/// </summary>
public sealed record EventCorrelation
{
    public required string Id { get; init; }
    public required string EventBedId { get; init; }
    public required string RegionalEvent { get; init; }
    public required AgeDist Age { get; init; }
    public required string SourceRef { get; init; }
}

/// <summary>
/// A reservoir-correction model (offset distribution + applicability window).
/// Revisions create new ids; the old version stays in the store untouched.
/// </summary>
public sealed record ReservoirModel
{
    public required string Id { get; init; }
    public required AgeDist Offset { get; init; }
    public required string AppliesTo { get; init; }
    public required string SourceRef { get; init; }
    public string? SupersedesId { get; init; }
}

public enum InterpolatorKind { PiecewiseLinear, MonotonePchip }

public enum DuplicateDepthPolicy { EnumerateCandidates, Reject }

/// <summary>
/// Versioned derivation parameters. Every derivation records the rule-set version used,
/// so results can be traced back to "which rules produced this".
/// </summary>
public sealed record RuleSet
{
    public required string Version { get; init; }
    public required int Realizations { get; init; }
    public required InterpolatorKind[] Interpolators { get; init; }
    public required DuplicateDepthPolicy DuplicatePolicy { get; init; }
    public required double MinMonotonicFraction { get; init; }
    public required int MaxCandidates { get; init; }
    public required double BandLow { get; init; }
    public required double BandHigh { get; init; }
    public required string Note { get; init; }

    public static RuleSet Default => new()
    {
        Version = "ruleset-v1",
        Realizations = 200,
        Interpolators = [InterpolatorKind.PiecewiseLinear, InterpolatorKind.MonotonePchip],
        DuplicatePolicy = DuplicateDepthPolicy.EnumerateCandidates,
        MinMonotonicFraction = 0.9,
        MaxCandidates = 64,
        BandLow = 0.05,
        BandHigh = 0.95,
        Note = "baseline rules"
    };
}

/// <summary>
/// An exclusion decision over an anchor. Decisions are layered on top of immutable
/// material and are themselves immutable history: revoking creates a new record.
/// Both adopted and revoked decisions are preserved.
/// </summary>
public sealed record Exclusion
{
    public required string Id { get; init; }
    public required string AnchorId { get; init; }
    public required string Reason { get; init; }
    public required string DecidedBy { get; init; }
    public required string DecidedAt { get; init; }
    public required bool Active { get; init; }
    public string? RevokedByEventId { get; init; }
}

public enum DiagSeverity { Info, Warning, Error }

/// <summary>A positioned diagnostic. Never silently dropped; attached to its source location.</summary>
public sealed record Diagnostic
{
    public required string Code { get; init; }
    public required DiagSeverity Severity { get; init; }

    /// <summary>Where the problem lives, e.g. "anchor:A07.depth" or "import:file.json:line 14".</summary>
    public required string Position { get; init; }

    public required string Message { get; init; }
    public string? ObjectId { get; init; }
}
