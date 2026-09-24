namespace ChronoCore.Domain.Engine;

/// <summary>Deterministic, seedable sampler. Quantile draws come from the
/// anchor distributions themselves (Gaussian CDF inversion for gaussian
/// kinds; empirical CDF interpolation otherwise), so long tails propagate
/// without ever collapsing to the mean.</summary>
public sealed class Sampler
{
    private ulong _state;

    public Sampler(uint seed) => _state = ((ulong)seed << 32) | 0x9E3779B97F4A7C15UL;

    public double NextUniform()
    {
        // SplitMix64 — stable across platforms/runtimes
        var z = (_state + 0x9E3779B97F4A7C15UL);
        z = (z ^ (z >> 30)) * 0xBF58476D1CE4E5B9UL;
        z = (z ^ (z >> 27)) * 0x94D049BB133111EBUL;
        z ^= z >> 31;
        // mutate state for next draw
        unchecked { _state = z + 0x9E3779B97F4A7C15UL; }
        return (z >> 11) * (1.0 / 9007199254740992.0);
    }
}

public static class QuantileMath
{
    /// <summary>Inverse normal CDF (Acklam), p in (0,1).</summary>
    public static double InvNormal(double p)
    {
        const double a1 = -39.6968302866538, a2 = 220.946098424521, a3 = -275.928510446969;
        const double a4 = 138.357751867269, a5 = -30.6647980661472, a6 = 2.50662827745924;
        const double b1 = -54.4760987982241, b2 = 161.585836858041, b3 = -155.698979859887;
        const double b4 = 66.8013118877197, b5 = -13.2806815528857;
        const double c1 = -0.00778489400243029, c2 = -0.322396458041136, c3 = -2.40075827716184;
        const double c4 = -2.54973253934373, c5 = 4.37466414146497, c6 = 2.93816398269878;
        const double d1 = 0.00778469570904146, d2 = 0.32246712907004, d3 = 2.445134137143;
        const double d4 = 3.75440866190742;
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

    /// <summary>Sample one age draw (already reservoir-corrected by caller)
    /// for a uniform p, honoring the declared distribution shape.</summary>
    public static double Draw(this Model.AgeDistribution dist, Sampler sampler)
    {
        var p = sampler.NextUniform();
        p = Math.Clamp(p, 1e-6, 1 - 1e-6);
        return dist.Kind switch
        {
            Model.DistributionKind.Gaussian => dist.Central + InvNormal(p) * dist.SigmaYoung,
            Model.DistributionKind.AsymmetricGaussian =>
                p < 0.5
                    ? dist.Central + InvNormal(p) * dist.SigmaYoung
                    : dist.Central + InvNormal(p) * dist.SigmaOld,
            Model.DistributionKind.EmpiricalQuantiles => dist.Q(p),
            Model.DistributionKind.Uniform => dist.Q(p),
            _ => dist.Q(p)
        };
    }

    public static double[] QuantilesOf(IReadOnlyList<double> sorted, double[] probs)
    {
        var outp = new double[probs.Length];
        var n = sorted.Count;
        for (var i = 0; i < probs.Length; i++)
        {
            var h = Math.Clamp(probs[i], 0, 1) * (n - 1);
            var lo = (int)Math.Floor(h);
            var hi = Math.Min(lo + 1, n - 1);
            outp[i] = sorted[lo] + (h - lo) * (sorted[hi] - sorted[lo]);
        }
        return outp;
    }
}

/// <summary>Monotone (shape-preserving) Hermite interpolation in depth-age.
/// Guarantees no overshoot between control points, which keeps the sampled
/// depth-age relation non-decreasing within a section.</summary>
public static class Pchip
{
    public static double Interpolate(double[] xs, double[] ys, double x)
    {
        if (x <= xs[0]) return ys[0];
        if (x >= xs[^1]) return ys[^1];
        var i = 1;
        while (i < xs.Length && xs[i] < x) i++;
        var x0 = xs[i - 1]; var x1 = xs[i];
        var y0 = ys[i - 1]; var y1 = ys[i];
        var h = x1 - x0;
        if (h <= 1e-9) return y1;
        var m0 = SecantSlope(xs, ys, i - 1);
        var m1 = SecantSlope(xs, ys, i);
        var t = (x - x0) / h;
        var t2 = t * t; var t3 = t2 * t;
        var h00 = 2 * t3 - 3 * t2 + 1;
        var h10 = t3 - 2 * t2 + t;
        var h01 = -2 * t3 + 3 * t2;
        var h11 = t3 - t2;
        return h00 * y0 + h10 * h * m0 + h01 * y1 + h11 * h * m1;
    }

    private static double SecantSlope(double[] xs, double[] ys, int i)
    {
        if (i <= 0) return (ys[1] - ys[0]) / (xs[1] - xs[0]);
        if (i >= xs.Length - 1) return (ys[^1] - ys[^2]) / (xs[^1] - xs[^2]);
        var h0 = xs[i] - xs[i - 1];
        var h1 = xs[i + 1] - xs[i];
        var s0 = (ys[i] - ys[i - 1]) / h0;
        var s1 = (ys[i + 1] - ys[i]) / h1;
        if (s0 * s1 <= 0) return 0;
        return (h0 + h1) / (h0 / s1 + h1 / s0);
    }
}
