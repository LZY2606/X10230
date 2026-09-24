using System.Text.Json.Serialization;

namespace AgeDepth.Core;

/// <summary>How a segment relates to the segment below it.</summary>
public enum SegmentContact
{
    Continuous = 0,
    Hiatus = 1,          // depth gap / unconformity — explicit boundary
    Unconformity = 2,    // erosional / angular — explicit boundary
    Faulted = 3,         // non-stratigraphic contact — monotonicity NOT required
    Inverted = 4         // overturned (folded/thrust) section
}

/// <summary>
/// A stratigraphic segment of a core. Depth increases downward; the lower contact
/// marks explicit boundaries that break monotonic interpolation.
/// </summary>
public sealed record Segment(
    string Id,
    QValue TopDepth,
    QValue BottomDepth,
    string Lithology,
    SegmentContact LowerContact,
    string? ContactNote = null);

/// <summary>Supported radiocarbon / geochronological age shapes, including long-tailed forms.</summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum DistributionKind
{
    Normal,        // symmetric measurement
    LogNormal,     // strictly positive, right skew
    SkewNormal,    // general long tail (shape alpha)
    Mixture,       // multi-modal radiocarbon calendar posterior
    Uniform        // hard-bounded expert range
}

/// <summary>
/// A radiometric age distribution. It is never collapsed to a mean: parameters and the
/// raw representation are retained and the engine samples the shape directly.
/// </summary>
public sealed record AgeDistribution(
    DistributionKind Kind,
    IReadOnlyList<double> Parameters,
    string Raw,
    string? EventId = null)
{
    public double Mean => Kind switch
    {
        DistributionKind.Normal => Parameters[0],
        DistributionKind.LogNormal => Math.Exp(Parameters[0] + Parameters[1] * Parameters[1] / 2),
        DistributionKind.Uniform => (Parameters[0] + Parameters[1]) / 2,
        _ => double.NaN // mixture/skew mean intentionally not used as a replacement
    };
}

/// <summary>
/// An age anchor: a measured age distribution at a depth. <see cref="Excluded"/> belongs to
/// the (mutable) decision layer and never deletes the immutable measurement.
/// </summary>
public sealed record AgeAnchor(
    string Id,
    QValue Depth,
    AgeDistribution Distribution,
    string MaterialCode,
    string LabCode,
    bool Excluded = false,
    string? ExclusionReason = null);

/// <summary>
/// Marine reservoir offset applied to a depth interval. Stored as a distribution so a
/// reservoir uncertainty is propagated, not averaged away.
/// </summary>
public sealed record ReservoirOffset(
    string Id,
    QValue FromDepth,
    QValue ToDepth,
    AgeDistribution DeltaR,
    string Raw);

/// <summary>A correlatable event layer (tephra, ash, isotopic excursion) at a depth.</summary>
public sealed record EventLayer(
    string Id,
    QValue Depth,
    string EventCode,
    QValue? ReferenceAge,
    string? CorrelationNote = null);

/// <summary>
/// A recorded exclusion decision. Immutable materials are never physically removed; this
/// keeps who/what excluded an anchor and why, so the decision is replayable.
/// </summary>
public sealed record ExclusionDecision(
    string Id,
    string TargetId,
    string TargetKind,
    bool Exclude,
    string Rationale,
    string DecidedBy,
    string AtEventId);

/// <summary>
/// The immutable material bundle. Everything the sedimentology group imported, exactly as
/// represented, sits here; derived normalization happens elsewhere.
/// </summary>
public sealed record MaterialBundle(
    string CoreId,
    string CoreName,
    string DepthDatum,
    string AgeDatum,
    IReadOnlyList<Segment> Segments,
    IReadOnlyList<AgeAnchor> Anchors,
    IReadOnlyList<ReservoirOffset> ReservoirOffsets,
    IReadOnlyList<EventLayer> Events,
    IReadOnlyList<ExclusionDecision> Exclusions)
{
    public IEnumerable<Diag> AllQuantityDiagnostics()
    {
        foreach (var s in Segments)
        {
            foreach (var d in s.TopDepth.Diagnostics) yield return d;
            foreach (var d in s.BottomDepth.Diagnostics) yield return d;
        }
        foreach (var a in Anchors)
            foreach (var d in a.Depth.Diagnostics) yield return d;
        foreach (var r in ReservoirOffsets)
        {
            foreach (var d in r.FromDepth.Diagnostics) yield return d;
            foreach (var d in r.ToDepth.Diagnostics) yield return d;
        }
        foreach (var e in Events)
        {
            foreach (var d in e.Depth.Diagnostics) yield return d;
            if (e.ReferenceAge is not null)
                foreach (var d in e.ReferenceAge.Diagnostics) yield return d;
        }
    }
}
