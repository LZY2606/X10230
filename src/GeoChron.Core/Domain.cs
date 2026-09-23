using System;
using System.Collections.Generic;

namespace GeoChron.Core;

/// <summary>Depth datum / coordinate basis, e.g. meters below seafloor.</summary>
public sealed record DepthDatum(string Id, string Name, string Basis, string Description);

/// <summary>
/// A continuous core section. Gaps and unconformities are explicit boundaries:
/// nothing is ever interpolated across them.
/// </summary>
public sealed record CoreSegment(
    string Id,
    string DatumId,
    string TopRaw,
    string BottomRaw,
    double TopM,
    double BottomM,
    string Unit,
    bool Inverted,
    string? BoundaryBelow);

/// <summary>
/// Radiometric age as a distribution, never collapsed to a mean.
/// kind: "normal" (mean/sd), "samples" (explicit long-tail sample vector), "uniform".
/// </summary>
public sealed record AgeDistribution(string Kind, string Unit, double Mean, double Sd, double[] Samples)
{
    public double Sample(Random rng) => Kind switch
    {
        "normal" => Mean + Sd * NextGaussian(rng),
        "samples" => Samples.Length > 0 ? Samples[rng.Next(Samples.Length)] : Mean,
        "uniform" => Mean + (rng.NextDouble() * 2.0 - 1.0) * Sd,
        _ => Mean
    };

    public double Median => Kind == "samples" && Samples.Length > 0 ? Quantile(Samples, 0.5) : Mean;

    public static double Quantile(double[] values, double q)
    {
        if (values.Length == 0) return double.NaN;
        var copy = (double[])values.Clone();
        Array.Sort(copy);
        var pos = q * (copy.Length - 1);
        var lo = (int)Math.Floor(pos);
        var hi = Math.Min(lo + 1, copy.Length - 1);
        return copy[lo] + (copy[hi] - copy[lo]) * (pos - lo);
    }

    public static double NextGaussian(Random rng)
    {
        var u1 = 1.0 - rng.NextDouble();
        var u2 = 1.0 - rng.NextDouble();
        return Math.Sqrt(-2.0 * Math.Log(u1)) * Math.Cos(2.0 * Math.PI * u2);
    }
}

/// <summary>Reservoir age offset with its own uncertainty and documented basis.</summary>
public sealed record ReservoirOffset(double Mean, double Sd, string Unit, string Basis)
{
    public double Sample(Random rng) => Mean + Sd * AgeDistribution.NextGaussian(rng);
}

public sealed record AgeAnchor(
    string Id,
    string SegmentId,
    string DepthRaw,
    double DepthM,
    AgeDistribution Distribution,
    ReservoirOffset? Offset,
    string? EventLayerId,
    string Source)
{
    /// <summary>Effective depositional-age sample: measured age minus reservoir offset.</summary>
    public double EffectiveSample(Random rng) =>
        Distribution.Sample(rng) - (Offset?.Sample(rng) ?? 0.0);

    public double EffectiveMedian => Distribution.Median - (Offset?.Mean ?? 0.0);
}

public sealed record EventLayer(
    string Id,
    string SegmentId,
    string DepthRaw,
    double DepthM,
    string Name,
    string CorrelatesTo);

/// <summary>Exclusion decisions are decisions, but they are always preserved.</summary>
public sealed record ExclusionDecision(string Id, string AnchorId, string Reason, string ByEventId, bool Active);

public static class DepthParser
{
    /// <summary>
    /// Parses "12.35", "12.35 m", "1235 cm". Unconvertible values stay in raw
    /// form at their original position and get a located diagnostic; the import
    /// never silently substitutes a default.
    /// </summary>
    public static double Parse(string raw, string path, List<Diagnostic> diags)
    {
        var text = (raw ?? string.Empty).Trim();
        var parts = text.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (parts.Length is 1 or 2 && double.TryParse(parts[0], System.Globalization.CultureInfo.InvariantCulture, out var v))
        {
            var unit = parts.Length == 2 ? parts[1].ToLowerInvariant() : "m";
            switch (unit)
            {
                case "m": return v;
                case "cm": return v / 100.0;
                case "mm": return v / 1000.0;
                default:
                    diags.Add(Diagnostic.Error("DEPTH-UNIT", $"未知深度单位 '{unit}'，值保留在原位置", path));
                    return double.NaN;
            }
        }
        diags.Add(Diagnostic.Error("DEPTH-FORMAT", $"无法解析深度值 '{raw}'，值保留在原位置", path));
        return double.NaN;
    }
}
