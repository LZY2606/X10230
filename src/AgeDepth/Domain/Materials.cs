namespace AgeDepth.Domain;

/// <summary>Immutable materials: imported once, never edited in place.</summary>
public sealed record DepthDatum(string Id, string Name, string Convention, string Unit);

public sealed record SegmentGap(string Id, DepthQuantity Top, DepthQuantity Bottom, string Reason);

public sealed record Unconformity(string Id, DepthQuantity Depth, string Kind, string Evidence);

public sealed record AgeAnchor(
    string Id,
    DepthQuantity Depth,
    AgeDistribution Age,
    string? ReservoirOffsetRef,
    string? EventLayerRef,
    string Source);

public sealed record ReservoirOffset(string Id, double Years, string Unit, string Rationale);

public sealed record EventLayer(string Id, DepthQuantity Depth, string Name, string? CorrelatedAnchorRef);

/// <summary>Exclusion decisions are preserved whether adopted or not.</summary>
public sealed record ExclusionDecision(string Id, string TargetRef, string Reason, string DecidedBy, bool Adopted);

/// <summary>Versioned derivation parameters (e.g. reservoir corrections).</summary>
public sealed record ParameterSet(
    string Version,
    IReadOnlyDictionary<string, double> ReservoirCorrections,
    double MonotonicToleranceYr);

public sealed record CoreSection(
    string SectionId,
    DepthDatum Datum,
    IReadOnlyList<SegmentGap> Gaps,
    IReadOnlyList<Unconformity> Unconformities,
    IReadOnlyList<AgeAnchor> Anchors,
    IReadOnlyList<ReservoirOffset> Offsets,
    IReadOnlyList<EventLayer> EventLayers,
    IReadOnlyList<ExclusionDecision> Exclusions);
