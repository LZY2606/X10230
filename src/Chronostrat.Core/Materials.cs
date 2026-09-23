namespace Chronostrat.Core;

/// <summary>
/// Immutable materials layer. Depth datum, segment gaps, radiometric age
/// distributions, reservoir offsets, event-layer correlations and every
/// exclusion decision are preserved verbatim; the mutable decisions layer
/// never rewrites them.
/// </summary>
public static class MaterialKinds
{
    public const string DepthDatum = "depth-datum";
    public const string Segment = "segment";
    public const string Boundary = "boundary";           // explicit gap / unconformity
    public const string AgeAnchor = "age-anchor";
    public const string ReservoirOffset = "reservoir-offset";
    public const string EventLayer = "event-layer";
    public const string ExclusionDecision = "exclusion-decision";
}

public static class BoundaryKinds
{
    public const string Gap = "gap";
    public const string Unconformity = "unconformity";
}

/// <summary>A continuous (unfaulted, unbroken) depth interval.</summary>
public sealed record Segment(
    string Id,
    double TopDepth,        // in datum unit, down-positive
    double BaseDepth,
    string Unit,
    string DatumId);

/// <summary>An explicit boundary between segments: a depth gap or an unconformity.</summary>
public sealed record Boundary(
    string Id,
    string Kind,            // BoundaryKinds
    double AtDepth,
    string Unit,
    string DatumId,
    string? Note);

/// <summary>Full age distribution — never collapsed to a mean.</summary>
public sealed record AgeDistribution(
    string Kind,            // "normal" | "uniform" | "empirical"
    string Unit,            // age unit, e.g. "ka"
    double? Mean,
    double? Sd,
    double? Min,
    double? Max,
    double[]? Samples       // empirical, keeps long tails
)
{
    public double Draw(Random rng) => Kind switch
    {
        "normal" => Mean!.Value + Sd!.Value * NextGaussian(rng),
        "uniform" => Min!.Value + (Max!.Value - Min!.Value) * rng.NextDouble(),
        "empirical" => Samples![rng.Next(Samples.Length)],
        _ => throw new InvalidOperationException($"unknown distribution kind {Kind}")
    };

    private static double NextGaussian(Random rng)
    {
        double u1 = 1.0 - rng.NextDouble();
        double u2 = rng.NextDouble();
        return Math.Sqrt(-2.0 * Math.Log(u1)) * Math.Cos(2.0 * Math.PI * u2);
    }

    public double Median() => Kind switch
    {
        "normal" => Mean!.Value,
        "uniform" => (Min!.Value + Max!.Value) / 2.0,
        "empirical" => Samples!.OrderBy(x => x).ElementAt(Samples.Length / 2),
        _ => double.NaN
    };
}

/// <summary>A radiometric / event age anchor at a depth. Raw representation kept.</summary>
public sealed record AgeAnchor(
    string Id,
    double Depth,
    string DepthUnit,
    string DatumId,
    AgeDistribution Age,
    string? ReservoirOffsetId,
    string Source);          // provenance, e.g. "lab-report:AMS-114"

public sealed record ReservoirOffset(
    string Id,
    AgeDistribution Offset); // subtracted from the anchor age

public sealed record EventLayer(
    string Id,
    string Name,
    double Depth,
    string DepthUnit,
    string DatumId,
    AgeDistribution? CorrelatedAge,
    string Confidence);      // "certain" | "tentative"

/// <summary>Every exclusion decision is preserved as first-class material.</summary>
public sealed record ExclusionDecision(
    string Id,
    string TargetId,         // anchor / event-layer id
    string Reason,
    string DecidedBy,
    string DecidedAt);

/// <summary>The immutable materials snapshot assembled from the event log.</summary>
public sealed record MaterialsSnapshot(
    DepthDatum? Datum,
    IReadOnlyList<Segment> Segments,
    IReadOnlyList<Boundary> Boundaries,
    IReadOnlyList<AgeAnchor> Anchors,
    IReadOnlyList<ReservoirOffset> ReservoirOffsets,
    IReadOnlyList<EventLayer> EventLayers,
    IReadOnlyList<ExclusionDecision> Exclusions,
    IReadOnlyList<Diagnostic> ImportDiagnostics)
{
    public bool IsExcluded(string targetId) => Exclusions.Any(e => e.TargetId == targetId);

    public AgeAnchor[] ActiveAnchors() =>
        Anchors.Where(a => !IsExcluded(a.Id)).ToArray();

    public EventLayer[] ActiveEventLayers() =>
        EventLayers.Where(e => !IsExcluded(e.Id)).ToArray();
}
