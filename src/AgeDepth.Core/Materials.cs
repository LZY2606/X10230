using System.Text.Json.Serialization;

namespace AgeDepth.Core;

/// <summary>
/// Immutable source materials. Revisions create a new record whose
/// <see cref="Material.Supersedes"/> points at the old identity; nothing is
/// ever edited in place or deleted. Raw unit/datum representations are kept.
/// </summary>
[JsonPolymorphic(TypeDiscriminatorPropertyName = "kind")]
[JsonDerivedType(typeof(DepthDatumMaterial), "depth-datum")]
[JsonDerivedType(typeof(SegmentGapMaterial), "segment-gap")]
[JsonDerivedType(typeof(RadiometricAgeMaterial), "radiometric-age")]
[JsonDerivedType(typeof(ReservoirOffsetMaterial), "reservoir-offset")]
[JsonDerivedType(typeof(EventLayerMaterial), "event-layer")]
public abstract record Material
{
    public string Id { get; init; } = "";
    public int Version { get; init; } = 1;
    public string? Supersedes { get; init; }
    /// <summary>Provenance: lab, file, operator, publication.</summary>
    public string Source { get; init; } = "";
    public string Note { get; init; } = "";
}

/// <summary>Depth reference frame, e.g. mcd with a published offset to mbsf.</summary>
public sealed record DepthDatumMaterial : Material
{
    public string DatumName { get; init; } = "mcd";
    public double OffsetToMbsfM { get; init; }
}

/// <summary>An explicit depth boundary: hiatus, unconformity or core gap.</summary>
public sealed record SegmentGapMaterial : Material
{
    public Quantity Top { get; init; } = new(0, "m", "mbsf");
    public Quantity Bottom { get; init; } = new(0, "m", "mbsf");
    /// <summary>hiatus | unconformity | core-gap</summary>
    public string GapKind { get; init; } = "core-gap";
}

public sealed record RadiometricAgeMaterial : Material
{
    public string LabCode { get; init; } = "";
    public Quantity Depth { get; init; } = new(0, "m", "mbsf");
    public AgeDist Age { get; init; } = new NormalDist(0, 1);
    public string AgeUnit { get; init; } = "ka";
    public string AgeDatum { get; init; } = "calBP";
    /// <summary>Optional reference to a reservoir-offset material.</summary>
    public string? ReservoirOffsetId { get; init; }
}

public sealed record ReservoirOffsetMaterial : Material
{
    public string Region { get; init; } = "";
    public double OffsetYr { get; init; }
    public double SdYr { get; init; }
}

public sealed record EventLayerMaterial : Material
{
    public string EventName { get; init; } = "";
    public Quantity Depth { get; init; } = new(0, "m", "mbsf");
    public AgeDist Age { get; init; } = new NormalDist(0, 1);
    public string AgeUnit { get; init; } = "ka";
    public string AgeDatum { get; init; } = "calBP";
}
