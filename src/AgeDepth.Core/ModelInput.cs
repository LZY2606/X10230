namespace AgeDepth.Core;

/// <summary>Everything the derived layer needs to build an age-depth model.</summary>
public sealed record ModelInput(
    DepthDatum Datum,
    IReadOnlyList<CoreSegment> Segments,
    IReadOnlyList<AgeAnchor> Anchors,
    IReadOnlyList<EventLayer> EventLayers,
    IReadOnlySet<string> ExcludedAnchorIds,
    ModelParams Params);
