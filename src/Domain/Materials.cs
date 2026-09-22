namespace ChronoCore.Domain;

// ---------------------------------------------------------------------------
// Immutable materials. These rows are evidence: imported once and never edited.
// Decisions (exclusions, parameter revisions) live separately as events.
// ---------------------------------------------------------------------------

public sealed record DepthDatum(string Name, string Description);

public sealed record SegmentMaterial(
    string Id,
    string CoreId,
    Quantity Top,
    Quantity Bottom,
    string Facies,
    string Raw);            // original representation as imported

public sealed record AnchorMaterial(
    string Id,
    string CoreId,
    Quantity Depth,
    AgeDistribution Age,
    string Method,          // "14C-planktonic" | "tephra" | "bioevent" ...
    string RawDepth,        // original textual depth, preserved on import
    string RawAge);         // original textual age, preserved on import

public sealed record EventBedMaterial(
    string Id,
    string CoreId,
    Quantity Depth,
    string Name,
    string? CorrelatesTo,   // e.g. "Hikurangi Tephra T-3, regional event id"
    string Raw);

public sealed record BoundaryMaterial(
    string Id,
    string CoreId,
    Quantity Depth,
    string Kind,            // "hiatus" | "fault" | "core-gap"
    string? MissingInterval,
    string Raw);

public sealed record ExclusionDecision(
    string Id,
    string TargetId,
    string TargetKind,      // "anchor" | "segment" | "event-bed"
    string Reason,
    string DecidedBy,
    string DecidedAt,
    string? RevokesDecisionId);   // non-null when this decision overturns an earlier one
