using System.Globalization;
using System.Text.Json.Serialization;

namespace AgeDepth.Domain;

/// <summary>Depth datum / vertical coordinate reference (immutable source material).</summary>
public sealed record DepthDatum(string Id, string Name, string RawReference, string VerticalCrs);

public enum BoundaryKind { Continuous, Gap, Unconformity }

/// <summary>One core segment. A missing span below the segment is an explicit boundary
/// (gap or unconformity); age interpolation may never cross it.</summary>
public sealed record CoreSegment(
    string Id,
    string CoreId,
    string RawTop,
    string RawBottom,
    double TopM,
    double BottomM,
    string DepthUnit,
    string DatumId,
    BoundaryKind LowerBoundary,
    string? LowerBoundaryRaw,
    bool IsInverted,
    string InversionEvidence);

/// <summary>Radiometric age distribution. The full sample set is the source of truth;
/// summaries (mean) are allowed for display only and never substitute for the distribution.</summary>
public sealed record AgeDistribution(string Kind, double[] Samples, string Raw)
{
    public double Quantile(double p)
    {
        if (Samples.Length == 0) return double.NaN;
        var sorted = (double[])Samples.Clone();
        Array.Sort(sorted);
        // nearest-rank quantile, stable on ties
        var rank = (int)Math.Ceiling(p * sorted.Length);
        rank = Math.Clamp(rank, 1, sorted.Length);
        return sorted[rank - 1];
    }

    public double Median => Quantile(0.5);
    public double QLo => Quantile(0.025);
    public double QHi => Quantile(0.975);

    [JsonIgnore] public double DisplayMean => Samples.Length == 0 ? double.NaN : Samples.Average();
}

/// <summary>A radiometric age anchor at a measured depth. ReservoirOffsetKa is the
/// corrected reservoir effect (ka); corrected apparent age = sample - offset.</summary>
public sealed record AgeAnchor(
    string Id,
    string SegmentId,
    string RawDepth,
    double DepthM,
    string DepthUnit,
    string DatumId,
    AgeDistribution Distribution,
    double ReservoirOffsetKa,
    string ReservoirOffsetRaw,
    string? EventLayerId,
    string Provenance,
    bool DepthConvertible);

/// <summary>Event layer (tephra / marker bed) with a correlation statement.
/// OlderThanLayerId encodes a known relative-age order used to detect inversion.</summary>
public sealed record EventLayer(string Id, string SegmentId, string Name, double DepthM,
    string Correlation, string? OlderThanLayerId, string RawDepth);

/// <summary>Immutable materials as they participate in a computation,
/// together with the versioned material references the digest is built from.</summary>
public sealed record MaterialState(
    DepthDatum Datum,
    IReadOnlyList<CoreSegment> Segments,
    IReadOnlyList<AgeAnchor> Anchors,
    IReadOnlyList<EventLayer> EventLayers,
    IReadOnlySet<string> ExcludedAnchorIds,
    IReadOnlyList<string> MaterialRefs);

public sealed record ModelParams(int MaxCandidates, double LoQ, double HiQ, string Version)
{
    public static readonly ModelParams P1 = new(64, 0.025, 0.975, "p1");
}

public enum ComputeStatus { Ok, IncompleteInput, BudgetExhausted, Unsolvable }

public sealed record Diagnostic(string Code, string Location, string Message);

public sealed record AnchorPoint(string AnchorId, double DepthM, double Median, double Lo, double Hi);

public sealed record RateInterval(
    string FromAnchorId,
    string ToAnchorId,
    double DepthFromM,
    double DepthToM,
    double RateLo,
    double RateHi,
    string Unit)
{
    public double RateMid => (RateLo + RateHi) / 2.0;
}

public sealed record Candidate(
    string Id,
    int Rank,
    IReadOnlyList<string> AnchorIds,
    IReadOnlyList<AnchorPoint> Points,
    IReadOnlyList<RateInterval> Rates);

public sealed record SegmentResult(
    string SegmentId,
    string InputDigest,
    ComputeStatus Status,
    IReadOnlyList<Diagnostic> Diagnostics,
    IReadOnlyList<Candidate> Candidates);

public sealed record ModelBoundary(string UpperSegmentId, string LowerSegmentId, BoundaryKind Kind, double DepthM);

public sealed record ModelResult(
    string ParamVersion,
    string OverallInputDigest,
    ComputeStatus Status,
    IReadOnlyList<Diagnostic> Diagnostics,
    IReadOnlyList<SegmentResult> SegmentResults,
    IReadOnlyList<ModelBoundary> Boundaries,
    IReadOnlyList<string> ReusedSegmentIds,
    IReadOnlyList<string> ExcludedAnchorIds);

public static class Units
{
    // Derived-layer normalisation. The raw representation is always retained on the material;
    // only derived values are normalised, and unconvertible units fail in place with a diagnostic.
    private static readonly Dictionary<string, double> ToMetres = new()
    {
        ["m"] = 1.0, ["meter"] = 1.0, ["meters"] = 1.0, ["metre"] = 1.0, ["metres"] = 1.0,
        ["cm"] = 0.01, ["centimetre"] = 0.01, ["centimeter"] = 0.01,
        ["mm"] = 0.001,
    };

    public static bool TryNormalizeDepth(string raw, string? unit, out double metres, out string? reason)
    {
        metres = 0;
        reason = null;
        if (!double.TryParse(raw.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out var v))
        {
            reason = $"unparseable depth value '{raw}'";
            return false;
        }
        if (unit is null || !ToMetres.TryGetValue(unit.Trim().ToLowerInvariant(), out var factor))
        {
            reason = $"unconvertible depth unit '{unit}' (no factor to metres)";
            return false;
        }
        metres = v * factor;
        return true;
    }

    public static double Fmt(double v) => Math.Round(v, 6);
}
