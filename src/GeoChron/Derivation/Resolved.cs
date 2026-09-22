using GeoChron.Domain;

namespace GeoChron.Derivation;

/// <summary>Depth resolved to metres relative to an explicit datum; raw text retained.</summary>
public sealed record DepthPoint(string Raw, double Metres, string Datum, bool Convertible, string? Unit);

/// <summary>A core segment. Top/bottom follow the core axis (increasing depth).</summary>
public sealed record CoreSeg(
    string Lineage, string Name, string Lithology,
    DepthPoint Top, DepthPoint Bottom, SegmentKind Kind,
    string? GapToNextLineage, BoundaryKind GapToNextKind);

public sealed record Gap(
    string Lineage, string Name, BoundaryKind Kind,
    DepthPoint? Upper, DepthPoint? Lower,
    string? UpperSegment, string? LowerSegment,
    double? MinGapM, double? DurationMinYr);

public sealed record Reservoir(string Lineage, string Name, ReservoirKind Kind, double DeltaR, double SigmaYr, string Source);

public sealed record EventLayer(
    string Lineage, string Name, string Marker, string Correlation,
    DepthPoint Depth, bool UsedAsAnchor, bool Excluded, string? ExcludeReason,
    double? AnchorAge14CBP, double? AnchorSigmaYr);

/// <summary>A usable radiometric anchor candidate (reservoir-corrected where applicable).</summary>
public sealed record Anchor(
    string Lineage, string Sample, string Method, string Basis, string AgeBasisRaw,
    double DepthM, DepthPoint DepthRaw,
    double Mean14CBP, double Sigma14Yr,
    DistKind Distribution, long TailSize,
    double[]? EmpiricalSamples14CBP,
    string? ReservoirLineage, bool Excluded, string? ExcludeReason,
    string MaterialRef);
