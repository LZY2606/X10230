using System.Text.Json.Nodes;

namespace ChronoScale.Core;

/// <summary>How a segment behaves at its contacts. Only Continuous intervals may be interpolated across.</summary>
public enum SegmentContinuity { Continuous, Unconformity, Gap, Inverted }

/// <summary>Immutable stratigraphic segment. Raw depth text is preserved on UnitValues.</summary>
public sealed record Segment(
    string Id,
    int Order,
    UnitValue TopDepth,
    UnitValue BottomDepth,
    SegmentContinuity Continuity,
    string Lithology,
    string? Note = null)
{
    public bool DepthsConvertible => TopDepth.IsConvertible && BottomDepth.IsConvertible;
    public bool IsMonotonic => DepthsConvertible && BottomDepth.Numeric!.Value > TopDepth.Numeric!.Value;

    public JsonObject ToNode() => new()
    {
        ["id"] = Id,
        ["order"] = Order,
        ["topDepth"] = TopDepth.ToNode(),
        ["bottomDepth"] = BottomDepth.ToNode(),
        ["continuity"] = Continuity.ToString().ToLowerInvariant(),
        ["lithology"] = Lithology,
        ["note"] = Note
    };
}

/// <summary>Immutable radiometric age anchor carrying its full distribution (never just a mean).</summary>
public sealed record AgeAnchor(
    string Id,
    UnitValue Depth,
    AgeDistribution Distribution,
    string Method,
    string? MaterialType,
    bool Excluded = false,
    string? ExclusionReason = null,
    string? ReservoirCorrectionId = null)
{
    public JsonObject ToNode() => new()
    {
        ["id"] = Id,
        ["depth"] = Depth.ToNode(),
        ["distribution"] = Distribution.ToNode(),
        ["method"] = Method,
        ["materialType"] = MaterialType,
        ["excluded"] = Excluded,
        ["exclusionReason"] = ExclusionReason,
        ["reservoirCorrectionId"] = ReservoirCorrectionId
    };
}

/// <summary>Reservoir offset (e.g. marine reservoir R) itself a distribution in time units.</summary>
public sealed record ReservoirCorrection(
    string Id,
    string Scope,
    AgeDistribution Offset,
    string? AppliesToAnchorId = null)
{
    public JsonObject ToNode() => new()
    {
        ["id"] = Id,
        ["scope"] = Scope,
        ["appliesToAnchorId"] = AppliesToAnchorId,
        ["offset"] = Offset.ToNode()
    };
}

/// <summary>Correlated event horizon (e.g. tephra / isotope spike) linking to an external age distribution.</summary>
public sealed record EventBed(
    string Id,
    UnitValue Depth,
    string Label,
    AgeDistribution CorrelationAge,
    string CorrelationBasis)
{
    public JsonObject ToNode() => new()
    {
        ["id"] = Id,
        ["depth"] = Depth.ToNode(),
        ["label"] = Label,
        ["correlationAge"] = CorrelationAge.ToNode(),
        ["correlationBasis"] = CorrelationBasis
    };
}

/// <summary>An explicit exclusion decision. Exclusions are never silently dropped.</summary>
public sealed record ExclusionDecision(
    string Id,
    string TargetId,
    string TargetKind,
    string Reason,
    string DecidedBy,
    bool RetainedButExcluded)
{
    public JsonObject ToNode() => new()
    {
        ["id"] = Id,
        ["targetId"] = TargetId,
        ["targetKind"] = TargetKind,
        ["reason"] = Reason,
        ["decidedBy"] = DecidedBy,
        ["retainedButExcluded"] = RetainedButExcluded
    };
}

/// <summary>The immutable imported evidence bundle. Raw representations live inside UnitValue.Raw / AgeDistribution.Raw.</summary>
public sealed record MaterialBundle(
    string BundleId,
    DepthDatum DepthDatum,
    TimeDatum TimeDatum,
    IReadOnlyList<Segment> Segments,
    IReadOnlyList<AgeAnchor> Anchors,
    IReadOnlyList<ReservoirCorrection> Reservoirs,
    IReadOnlyList<EventBed> EventBeds,
    IReadOnlyList<ExclusionDecision> Exclusions,
    IReadOnlyList<Diagnostic> ImportDiagnostics,
    string SourceRef)
{
    public JsonObject ToNode() => new()
    {
        ["bundleId"] = BundleId,
        ["sourceRef"] = SourceRef,
        ["depthDatum"] = DepthDatum.ToNode(),
        ["timeDatum"] = TimeDatum.ToNode(),
        ["segments"] = new JsonArray(Segments.Select(s => s.ToNode()).ToArray()),
        ["anchors"] = new JsonArray(Anchors.Select(a => a.ToNode()).ToArray()),
        ["reservoirs"] = new JsonArray(Reservoirs.Select(r => r.ToNode()).ToArray()),
        ["eventBeds"] = new JsonArray(EventBeds.Select(e => e.ToNode()).ToArray()),
        ["exclusions"] = new JsonArray(Exclusions.Select(e => e.ToNode()).ToArray()),
        ["importDiagnostics"] = new JsonArray(ImportDiagnostics.Select(d => d.ToNode()).ToArray())
    };
}
