namespace CoreChronology;

/// <summary>
/// Full probability distribution of an anchor's age. It must never be
/// silently replaced by its mean: sampling and quantiles propagate the
/// whole distribution, including long, asymmetric tails.
/// </summary>
public abstract record AgeDistribution
{
    public abstract double Quantile(double p, DeterministicRng rng);
    public double Sample(DeterministicRng rng) => Quantile(rng.NextDouble(), rng);

    public static double QuantileOf(IReadOnlyList<double> sortedSamples, double p)
    {
        if (sortedSamples.Count == 0)
            throw new InvalidOperationException("empty sample set");
        double pos = Math.Clamp(p, 0d, 1d) * (sortedSamples.Count - 1);
        int lo = (int)Math.Floor(pos);
        int hi = Math.Min(lo + 1, sortedSamples.Count - 1);
        double frac = pos - lo;
        return sortedSamples[lo] * (1 - frac) + sortedSamples[hi] * frac;
    }
}

public sealed record GaussianDistribution(double MeanCalYrBp, double SigmaYr) : AgeDistribution
{
    public override double Quantile(double p, DeterministicRng rng) =>
        MeanCalYrBp + SigmaYr * NormalInverseCdf(p);

    internal static double NormalInverseCdf(double p)
    {
        // Peter Acklam's rational approximation.
        const double a1 = -39.6968302866538, a2 = 220.946098424521, a3 = -275.928510446969;
        const double a4 = 138.357751867269, a5 = -30.6647980661472, a6 = 2.50662827745924;
        const double b1 = -54.4760987982241, b2 = 161.585836858041, b3 = -155.698979859887;
        const double b4 = 66.8013118877197, b5 = -13.2806815528857;
        const double c1 = -0.00778489400243029, c2 = -0.322396458041136, c3 = -2.40075827716184;
        const double c4 = -2.54973253934373, c5 = 4.37466414146497, c6 = 2.93816398269878;
        const double d1 = 0.00778469570904146, d2 = 0.32246712907004, d3 = 2.445134137143;
        const double d4 = 3.75440866190742;
        const double low = 0.02425, high = 1 - low;
        double q, r;
        if (p < low)
        {
            q = Math.Sqrt(-2 * Math.Log(p));
            return (((((c1 * q + c2) * q + c3) * q + c4) * q + c5) * q + c6) /
                   ((((d1 * q + d2) * q + d3) * q + d4) * q + 1);
        }
        if (p <= high)
        {
            q = p - 0.5; r = q * q;
            return (((((a1 * r + a2) * r + a3) * r + a4) * r + a5) * r + a6) * q /
                   (((((b1 * r + b2) * r + b3) * r + b4) * r + b5) * r + 1);
        }
        q = Math.Sqrt(-2 * Math.Log(1 - p));
        return -(((((c1 * q + c2) * q + c3) * q + c4) * q + c5) * q + c6) /
                ((((d1 * q + d2) * q + d3) * q + d4) * q + 1);
    }
}

public sealed record UniformDistribution(double LowCalYrBp, double HighCalYrBp) : AgeDistribution
{
    public override double Quantile(double p, DeterministicRng rng) =>
        LowCalYrBp + (HighCalYrBp - LowCalYrBp) * p;
}

/// <summary>
/// Empirical calibration-style distribution from (age, weight) points.
/// A long tail is just an ordinary tail here: no clipping, no mean replacement.
/// </summary>
public sealed record EmpiricalDistribution(IReadOnlyList<(double AgeCalYrBp, double Weight)> Points) : AgeDistribution
{
    private readonly double[] _edges = BuildEdges(Points);

    private static double[] BuildEdges(IReadOnlyList<(double Age, double Weight)> points)
    {
        double total = points.Sum(p => Math.Max(0, p.Weight));
        var edges = new double[points.Count + 1];
        double acc = 0;
        for (int i = 0; i < points.Count; i++)
        {
            edges[i] = acc / total;
            acc += Math.Max(0, points[i].Weight);
        }
        edges[^1] = 1.0;
        return edges;
    }

    public override double Quantile(double p, DeterministicRng rng)
    {
        p = Math.Clamp(p, 0d, 1d);
        int i = Array.BinarySearch(_edges, p);
        if (i < 0) i = ~i - 1;
        i = Math.Clamp(i, 0, Points.Count - 1);
        double lo = _edges[i], hi = _edges[i + 1];
        double frac = hi <= lo ? 0 : (p - lo) / (hi - lo);
        return Points[i].AgeCalYrBp +
               (Points[Math.Min(i + 1, Points.Count - 1)].AgeCalYrBp - Points[i].AgeCalYrBp) *
               (i == Points.Count - 1 ? 0 : frac);
    }
}
