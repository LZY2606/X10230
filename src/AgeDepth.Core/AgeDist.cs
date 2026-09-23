namespace AgeDepth.Core;

/// <summary>Sampling and quantiles for anchor age distributions. Fully seeded/deterministic.</summary>
public static class AgeDist
{
    public static double NextGaussian(Random rng)
    {
        var u1 = 1.0 - rng.NextDouble();
        var u2 = 1.0 - rng.NextDouble();
        return Math.Sqrt(-2.0 * Math.Log(u1)) * Math.Cos(2.0 * Math.PI * u2);
    }

    /// <summary>Draw one age in the distribution's own unit.</summary>
    public static double Sample(AgeDistribution d, Random rng) => d.Kind switch
    {
        "point" => d.Mean ?? 0.0,
        "normal" => (d.Mean ?? 0.0) + (d.Sd ?? 0.0) * NextGaussian(rng),
        "uniform" => (d.Min ?? 0.0) + ((d.Max ?? 0.0) - (d.Min ?? 0.0)) * rng.NextDouble(),
        "longtail" => Math.Exp((d.Mu ?? 0.0) + (d.Sigma ?? 1.0) * NextGaussian(rng)),
        "empirical" => d.Samples is { Length: > 0 } s ? s[rng.Next(s.Length)] : 0.0,
        _ => double.NaN
    };

    /// <summary>Deterministic quantile summary (p2.5, median, p97.5, mean) in the distribution's unit.</summary>
    public static (double P25, double Median, double P975, double Mean) Quantiles(AgeDistribution d, int seed, int n = 4000)
    {
        var rng = new Random(seed);
        var draws = new double[n];
        for (var i = 0; i < n; i++) draws[i] = Sample(d, rng);
        Array.Sort(draws);
        double Q(double p) => draws[Math.Clamp((int)Math.Round(p * (n - 1)), 0, n - 1)];
        return (Q(0.025), Q(0.5), Q(0.975), draws.Average());
    }

    /// <summary>Convert an age value to years BP, preserving unconvertible values as NaN.</summary>
    public static double ToYearsBp(double value, string unit) => unit switch
    {
        "yr BP" => value,
        "ka BP" => value * 1000.0,
        _ => double.NaN
    };

    public static bool IsConvertible(string unit) => unit is "yr BP" or "ka BP";
}
