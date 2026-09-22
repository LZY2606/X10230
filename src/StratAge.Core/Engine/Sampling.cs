namespace StratAge.Core.Engine;

/// <summary>Deterministic, seedable PRNG so replay and local recompute are stable.</summary>
public struct SplitMix
{
    ulong _s;
    public SplitMix(ulong seed) => _s = seed;
    public ulong NextU64()
    {
        _s += 0x9E3779B97F4A7C15UL;
        var z = _s;
        z = (z ^ (z >> 30)) * 0xBF58476D1CE4E5B9UL;
        z = (z ^ (z >> 27)) * 0x94D049BB133111EBUL;
        return z ^ (z >> 31);
    }
    public double NextDouble() => (NextU64() >> 11) * (1.0 / 9007199254740992.0);
}

public static class Quantiles
{
    /// <summary>Sample an age distribution. Param convention per shape:
    /// Normal(mu,sigma); LogNormal(muLog,sigmaLog); Uniform(low,high); StudentT(df, location, scale).</summary>
    public static double[] Sample(Domain.DistributionShape shape, double p1, double p2, double? p3,
        int draws, ulong seed)
    {
        var rng = new SplitMix(seed);
        var xs = new double[draws];
        for (var i = 0; i < draws; i++)
        {
            xs[i] = shape switch
            {
                Domain.DistributionShape.Normal => p1 + p2 * NormalInv(rng.NextDouble()),
                Domain.DistributionShape.LogNormal => Math.Exp(p1 + p2 * NormalInv(rng.NextDouble())),
                Domain.DistributionShape.Uniform => p1 + (p2 - p1) * rng.NextDouble(),
                Domain.DistributionShape.StudentT => p2 + (p3 ?? 1.0) * StudentT(rng.NextDouble(), p1),
                _ => throw new ArgumentOutOfRangeException(nameof(shape))
            };
        }
        return xs;
    }

    /// <summary>Subtract reservoir offset draws from age draws (paired via deterministic seed).</summary>
    public static double[] ApplyOffset(double[] age, Domain.DistributionShape shape, double p1, double p2, ulong seed)
    {
        var off = Sample(shape, p1, p2, null, age.Length, seed);
        var r = new double[age.Length];
        for (var i = 0; i < r.Length; i++) r[i] = age[i] - off[i];
        return r;
    }

    static double NormalInv(double p)
    {
        // Acklam inverse normal CDF
        const double a1 = -39.6968302866538, a2 = 220.946098424521, a3 = -275.928510446969;
        const double a4 = 138.357751867269, a5 = -30.6647980661472, a6 = 2.50662827745924;
        const double b1 = -54.4760987982241, b2 = 161.585836858041, b3 = -155.698979859887;
        const double b4 = 66.8013118877197, b5 = -13.2806815528857;
        const double c1 = -0.00778489400243029, c2 = -0.322396458041136, c3 = -2.40075827716184;
        const double c4 = -2.54973253934373, c5 = 4.37466414146497, c6 = 2.93816398269878;
        const double d1 = 0.00778469570904146, d2 = 0.32246712907004, d3 = 2.445134137143, d4 = 3.75440866190742;
        const double pLow = 0.02425, pHigh = 1 - pLow;
        double q, r;
        if (p < pLow)
        {
            q = Math.Sqrt(-2 * Math.Log(p));
            return (((((c1 * q + c2) * q + c3) * q + c4) * q + c5) * q + c6) /
                   ((((d1 * q + d2) * q + d3) * q + d4) * q + 1);
        }
        if (p <= pHigh)
        {
            q = p - 0.5; r = q * q;
            return (((((a1 * r + a2) * r + a3) * r + a4) * r + a5) * r + a6) * q /
                   (((((b1 * r + b2) * r + b3) * r + b4) * r + b5) * r + 1);
        }
        q = Math.Sqrt(-2 * Math.Log(1 - p));
        return -(((((c1 * q + c2) * q + c3) * q + c4) * q + c5) * q + c6) /
                ((((d1 * q + d2) * q + d3) * q + d4) * q + 1);
    }

    static double StudentT(double p, double df)
    {
        // inverse t via inverse normal first order correction (df>=1); sufficient for MC tails
        var z = NormalInv(p);
        var g1 = (z * z * z + z) / 4;
        var g2 = (5 * Math.Pow(z, 5) + 16 * Math.Pow(z, 3) + 3 * z) / 96;
        return z + g1 / df + g2 / (df * df);
    }

    public static double Quantile(double[] xs, double p)
    {
        var s = (double[])xs.Clone();
        Array.Sort(s);
        var h = (s.Length - 1) * p;
        var lo = (int)Math.Floor(h); var hi = (int)Math.Ceiling(h);
        return lo == hi ? s[lo] : s[lo] + (h - lo) * (s[hi] - s[lo]);
    }

    public static double Mean(double[] xs)
    {
        double t = 0; foreach (var x in xs) t += x; return t / xs.Length;
    }

    /// <summary>Fraction of draw pairs violating deeper-must-be-older (age1 shallower, age2 deeper).</summary>
    public static double InversionFraction(double[] shallower, double[] deeper)
    {
        long bad = 0;
        for (var i = 0; i < shallower.Length; i++)
            if (shallower[i] > deeper[i]) bad++;
        return (double)bad / shallower.Length;
    }
}
