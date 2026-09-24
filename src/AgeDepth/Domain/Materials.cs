using System.Text.Json.Serialization;

namespace AgeDepth.Domain;

/// <summary>Depth reference frame, e.g. metres below sea floor.</summary>
public sealed record DepthDatum(string Id, string Name, string Unit, string? Note);

/// <summary>
/// A stratigraphic section. <see cref="IsInverted"/> marks a tectonically inverted
/// section that must never be forced into normal superposition.
/// </summary>
public sealed record Segment
{
    public required string Id { get; init; }
    public required string Name { get; init; }
    public required Quantity TopDepth { get; init; }
    public required Quantity BottomDepth { get; init; }
    public string Lithology { get; init; } = "";
    public bool IsInverted { get; init; }
    public string? SourceRef { get; init; }
}

/// <summary>Explicit boundary between segments: a depositional gap or an unconformity.</summary>
public sealed record Gap
{
    public required string Id { get; init; }
    public required string AboveSegmentId { get; init; }
    public required string BelowSegmentId { get; init; }
    public required Quantity Depth { get; init; }
    /// <summary>"hiatus" (depositional gap) or "unconformity".</summary>
    public required string Kind { get; init; }
    public Quantity? MissingAgeEstimate { get; init; }
    public string? SourceRef { get; init; }
}

/// <summary>
/// A dated point. The radiometric result is a distribution (ordered samples), never a
/// scalar: the mean cannot replace it. <see cref="RawDepth"/> keeps the original
/// representation; <see cref="Depth"/> is the normalised derived value.
/// </summary>
public sealed record AgeAnchor
{
    public required string Id { get; init; }
    public required string SegmentId { get; init; }
    public required string Method { get; init; }
    public required string Material { get; init; }
    public required Quantity RawDepth { get; init; }
    public required Quantity Depth { get; init; }
    public required string AgeUnit { get; init; }
    public required IReadOnlyList<double> AgeSamples { get; init; }
    public string? ReservoirOffsetId { get; init; }
    public string? SourceRef { get; init; }
    public bool Excluded { get; init; }

    [JsonIgnore]
    public bool IsLongTail => AgeSamples.Count > 0 &&
        (Percentile(AgeSamples, 0.975) - Percentile(AgeSamples, 0.5)) >
        3.0 * (Percentile(AgeSamples, 0.5) - Percentile(AgeSamples, 0.025));

    public static double Percentile(IReadOnlyList<double> sortedAscending, double p)
    {
        if (sortedAscending.Count == 0) return double.NaN;
        var pos = p * (sortedAscending.Count - 1);
        var lo = (int)Math.Floor(pos);
        var hi = (int)Math.Ceiling(pos);
        return lo == hi
            ? sortedAscending[lo]
            : sortedAscending[lo] * (hi - pos) + sortedAscending[hi] * (pos - lo);
    }
}

/// <summary>Reservoir (e.g. marine carbon) age offset, itself a distribution in ka.</summary>
public sealed record ReservoirOffset
{
    public required string Id { get; init; }
    public required string Name { get; init; }
    public required string AgeUnit { get; init; }
    public required IReadOnlyList<double> AgeSamples { get; init; }
    public string? SourceRef { get; init; }
}

/// <summary>Correlated event layer (e.g. tephra) with an external age distribution.</summary>
public sealed record EventLayer
{
    public required string Id { get; init; }
    public required string Name { get; init; }
    public required Quantity RawDepth { get; init; }
    public required Quantity Depth { get; init; }
    public required string AgeUnit { get; init; }
    public required IReadOnlyList<double> AgeSamples { get; init; }
    public required string Correlation { get; init; }
    public string? SourceRef { get; init; }
}

/// <summary>An exclusion decision is itself evidence: it is kept, never silently dropped.</summary>
public sealed record ExclusionDecision
{
    public required string Id { get; init; }
    public required string TargetId { get; init; }
    public required string TargetKind { get; init; }
    public required string Reason { get; init; }
    public required string DecidedBy { get; init; }
    public required string DecidedAt { get; init; }
    public bool Reinstated { get; init; }
}
