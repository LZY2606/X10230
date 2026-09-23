using System.Collections.Generic;
using Chrono.Core.Materials;
using Chrono.Core.Quantities;

namespace Chrono.Core.Model;

/// <summary>
/// A usable age control point inside one continuous track. It carries the full effective age
/// distribution (measured minus reservoir offset), never a scalar mean.
/// </summary>
public sealed record Anchor(
    string Id,
    Depth Depth,
    IAgeDistribution EffectiveAge,
    string Kind,
    string SourceMaterialId)
{
    public string CorrelationGroup { get; init; } = "";
}

/// <summary>One continuous, interpolation-legal interval bounded by gaps/unconformities/faults.</summary>
public sealed record Track(
    string Id,
    string CoreId,
    Depth Top,
    Depth Bottom,
    IReadOnlyList<Anchor> Anchors,
    IReadOnlyList<string> BoundaryMaterialIds)
{
    /// <summary>Stable display identity of the uncertainty band at a grid depth.</summary>
    public string BandPointId(int gridIndex) => $"{Id}::band[{gridIndex}]";
    public string RatePointId(int intervalIndex) => $"{Id}::rate[{intervalIndex}]";
}

public sealed record QuantilePoint(string Id, Depth Depth, double Q025, double Q500, double Q975);

public sealed record CandidateModel(
    string Id,
    string TrackId,
    IReadOnlyList<string> AnchorIds,
    string Strategy,
    double Score,
    bool Feasible,
    double MonotonicityProbability,
    long FeasibleDraws,
    long TotalDraws,
    IReadOnlyList<QuantilePoint> AgeBand,
    IReadOnlyList<QuantilePoint> RateIntervals,
    IReadOnlyList<string> Diagnostics)
{
    /// <summary>True when equally defensible with another candidate: ties are all retained.</summary>
    public bool EquallyOptimal { get; init; }
    public bool Adopted { get; init; }
}

public sealed record TrackResult(
    Track Track,
    string Status,
    IReadOnlyList<CandidateModel> Candidates,
    IReadOnlyList<string> Diagnostics);
