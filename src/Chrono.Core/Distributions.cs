using System.Text.Json.Serialization;

namespace Chrono.Core;

/// <summary>
/// Parametric age distribution. A distribution is NEVER collapsed to a mean:
/// the engine propagates the full distribution through quantile samples.
/// </summary>
public sealed record DistributionSpec
{
    public required string Type { get; init; } // normal | lognormal | gamma | uniform | point
    public double Mean { get; init; }
    public double? Sigma { get; init; }
    public double? Min { get; init; }
    public double? Max { get; init; }
    public double? Shape { get; init; }
    public double? Scale { get; init; }

    public static DistributionSpec Point(double v) => new() { Type = "point", Mean = v };
    public static DistributionSpec Normal(double mean, double sigma) => new() { Type = "normal", Mean = mean, Sigma = sigma };
    public static DistributionSpec LogNormal(double meanLog, double sigmaLog) => new() { Type = "lognormal", Mean = meanLog, Sigma = sigmaLog };
    public static DistributionSpec GammaDist(double shape, double scale) => new() { Type = "gamma", Shape = shape, Scale = scale, Mean = shape * scale };
    public static DistributionSpec Uniform(double min, double max) => new() { Type = "uniform", Min = min, Max = max, Mean = (min + max) / 2.0 };

    public double Quantile(double p)
    {
        p = Math.Clamp(p, 1e-9, 1 - 1e-9);
        return Type switch
        {
            "point" => Mean,
            "normal" => Mean + (Sigma ?? 0) * NormalQuantile(p),
            "uniform" => (Min ?? 0) + p * ((Max ?? 0) - (Min ?? 0)),
            "lognormal" => Math.Exp(Mean + (Sigma ?? 0) * NormalQuantile(p)),
            "gamma" => GammaQuantile(Shape ?? 1, Scale ?? 1, p),
            _ => Mean
        };
    }

    public bool IsLongTailed() => Type is "lognormal" or "gamma";

    /// <summary>Quantile summary used for display; keeps skew visible (q95 != 2*mean - q05).</summary>
    public (double q05, double q50, double q95) Summary() => (Quantile(0.05), Quantile(0.5), Quantile(0.95));

    // Acklam's rational approximation of the inverse standard normal CDF.
    public static double NormalQuantile(double p)
    {
        double[] a = { -3.969683028665376e+01, 2.209460984245205e+02, -2.759285104469687e+02, 1.383577518672690e+02, -3.066479806614716e+01, 2.506628277459239e+00 };
        double[] b = { -5.447609879822406e+01, 1.615858368580409e+02, -1.556989798598866e+02, 6.680131188771972e+01, -1.328068155288572e+01 };
        double[] c = { -7.784894002430293e-03, -3.223964580411365e-01, -2.400758277161838e+00, -2.549732539343734e+00, 4.374664141464968e+00, 2.938163982698783e+00 };
        double[] d = { 7.784695709041462e-03, 3.224671290700398e-01, 2.445134137142996e+00, 3.754408661907416e+00 };
        const double plow = 0.02425, phigh = 1 - plow;
        if (p < plow)
        {
            var q = Math.Sqrt(-2 * Math.Log(p));
            return (((((c[0] * q + c[1]) * q + c[2]) * q + c[3]) * q + c[4]) * q + c[5]) /
                   ((((d[0] * q + d[1]) * q + d[2]) * q + d[3]) * q + 1);
        }
        if (p > phigh)
        {
            var q = Math.Sqrt(-2 * Math.Log(1 - p));
            return -(((((c[0] * q + c[1]) * q + c[2]) * q + c[3]) * q + c[4]) * q + c[5]) /
                     ((((d[0] * q + d[1]) * q + d[2]) * q + d[3]) * q + 1);
        }
        var qq = p - 0.5;
        var r = qq * qq;
        return (((((a[0] * r + a[1]) * r + a[2]) * r + a[3]) * r + a[4]) * r + a[5]) * qq /
               (((((b[0] * r + b[1]) * r + b[2]) * r + b[3]) * r + b[4]) * r + 1);
    }

    // Wilson-Hilferty approximation for the gamma quantile (accurate enough for band display).
    public static double GammaQuantile(double shape, double scale, double p)
    {
        var z = NormalQuantile(p);
        var t = 1.0 - 1.0 / (9.0 * shape) + z / (3.0 * Math.Sqrt(shape));
        return shape * scale * t * t * t;
    }
}

/// <summary>
/// Effective distribution of an anchor after applying a reservoir offset
/// (age = raw + offset). Deterministic stratified convolution, fixed grid.
/// </summary>
public static class EffectiveDistribution
{
    public const int GridSize = 401;

    public static double[] QuantileGrid(DistributionSpec raw, DistributionSpec? offset)
    {
        var grid = new double[GridSize];
        if (offset is null)
        {
            for (var i = 0; i < GridSize; i++) grid[i] = raw.Quantile((i + 0.5) / GridSize);
            return grid;
        }
        // convolution of independent raw and offset via deterministic stratified pairing
        for (var i = 0; i < GridSize; i++)
        {
            var p = (i + 0.5) / GridSize;
            var j = (i * 37 + 11) % GridSize; // fixed coprime-ish permutation, deterministic
            var q = (j + 0.5) / GridSize;
            grid[i] = raw.Quantile(p) + offset.Quantile(q);
        }
        Array.Sort(grid);
        return grid;
    }

    public static (double q05, double q50, double q95) SummaryOf(double[] grid) =>
        (Interp(grid, 0.05), Interp(grid, 0.5), Interp(grid, 0.95));

    public static double Interp(double[] sortedGrid, double p)
    {
        var x = p * (sortedGrid.Length - 1);
        var i = (int)Math.Floor(x);
        var f = x - i;
        if (i >= sortedGrid.Length - 1) return sortedGrid[^1];
        return sortedGrid[i] * (1 - f) + sortedGrid[i + 1] * f;
    }
}
