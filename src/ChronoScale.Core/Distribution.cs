using System.Globalization;
using System.Text.Json.Nodes;

namespace ChronoScale.Core;

/// <summary>Kind of a radioisotopic / age distribution. A distribution is never replaced by its mean.</summary>
public enum DistributionKind { Empirical, Normal, LogNormal, Mixture, Unconvertible }

/// <summary>
/// A full age distribution. Empirical forms keep every sample (long tail included).
/// Parametric forms are materialized with a fixed seed so quantiles are deterministic across machines.
/// </summary>
public sealed record AgeDistribution(
    DistributionKind Kind,
    string TimeUnit,
    IReadOnlyList<double> Samples,
    IReadOnlyList<AgeDistribution>? Components = null,
    IReadOnlyList<double>? Weights = null,
    string? Raw = null)
{
    public bool IsValid => Samples.Count >= 2 || Kind == DistributionKind.Unconvertible;

    public static AgeDistribution Unconvertible(string unit, string? raw)
        => new(DistributionKind.Unconvertible, unit, Array.Empty<double>(), Raw: raw);

    public double Quantile(double p)
    {
        if (Samples.Count == 0) return double.NaN;
        var s = Samples;
        double h = (s.Count - 1) * p;
        int lo = (int)Math.Floor(h);
        int hi = (int)Math.Ceiling(h);
        if (lo < 0) lo = 0;
        if (hi >= s.Count) hi = s.Count - 1;
        double frac = h - Math.Floor(h);
        var sorted = QuantileCache();
        return sorted[lo] * (1 - frac) + sorted[hi] * frac;
    }

    private double[]? _sorted;
    public double[] QuantileCache()
    {
        if (_sorted is not null) return _sorted;
        var copy = Samples.ToArray();
        Array.Sort(copy);
        _sorted = copy;
        return copy;
    }

    public double Median => Quantile(0.5);

    public JsonObject ToNode()
    {
        var n = new JsonObject
        {
            ["kind"] = Kind.ToString().ToLowerInvariant(),
            ["timeUnit"] = TimeUnit,
            ["sampleCount"] = Samples.Count,
            ["raw"] = Raw
        };
        if (Samples.Count > 0)
        {
            n["q025"] = Quantile(0.025).ToString(CultureInfo.InvariantCulture);
            n["median"] = Median.ToString(CultureInfo.InvariantCulture);
            n["q975"] = Quantile(0.975).ToString(CultureInfo.InvariantCulture);
            n["max"] = Samples.Max().ToString(CultureInfo.InvariantCulture);
        }
        return n;
    }
}
