namespace AgeDepth.Core;

/// <summary>
/// An anchor age distribution. Never collapsed to a mean: quantile evaluation
/// keeps the full shape (including long tails) through the whole pipeline.
/// </summary>
public sealed record AgeDistribution(string Kind, double[] P)
{
    public static AgeDistribution Point(double mu) => new("point", new[] { mu });
    public static AgeDistribution Normal(double mu, double sigma) => new("normal", new[] { mu, sigma });
    public static AgeDistribution Uniform(double min, double max) => new("uniform", new[] { min, max });
    public static AgeDistribution Samples(IEnumerable<double> values) =>
        new("samples", values.OrderBy(v => v).ToArray());

    public double Quantile(double q)
    {
        q = Math.Clamp(q, 0.0, 1.0);
        switch (Kind)
        {
            case "point": return P[0];
            case "normal": return P[0] + P[1] * NormInv(q);
            case "uniform": return P[0] + q * (P[1] - P[0]);
            case "samples":
                if (P.Length == 1) return P[0];
                var pos = q * (P.Length - 1);
                var lo = (int)Math.Floor(pos);
                var hi = Math.Min(lo + 1, P.Length - 1);
                return P[lo] + (P[hi] - P[lo]) * (pos - lo);
            default: throw new InvalidOperationException($"unknown distribution kind '{Kind}'");
        }
    }

    public double Median => Quantile(0.5);

    public double Mean => Kind switch
    {
        "point" => P[0],
        "normal" => P[0],
        "uniform" => (P[0] + P[1]) / 2.0,
        "samples" => P.Average(),
        _ => throw new InvalidOperationException($"unknown distribution kind '{Kind}'")
    };

    /// <summary>Shift the whole distribution by a constant (e.g. reservoir offset correction).</summary>
    public AgeDistribution Shift(double delta) => Kind switch
    {
        "point" or "normal" or "uniform" => this with { P = P.Select(v => v + delta).ToArray() },
        "samples" => this with { P = P.Select(v => v + delta).ToArray() },
        _ => this
    };

    // Acklam's inverse normal CDF approximation.
    private static double NormInv(double p)
    {
        if (p <= 0) return -6.5;
        if (p >= 1) return 6.5;
        double[] a = { -3.969683028665376e+01, 2.209460984245205e+02, -2.759285104469687e+02, 1.383577518672690e+02, -3.066479806614716e+01, 2.506628277459239e+00 };
        double[] b = { -5.447609879822406e+01, 1.615858368580409e+02, -1.556989798598866e+02, 6.680131188771972e+01, -1.328068155288572e+01 };
        double[] c = { -7.784894002430293e-03, -3.223964580411365e-01, -2.400758277161838e+00, -2.549732539343734e+00, 4.374664141464968e+00, 2.938163982698783e+00 };
        double[] d = { 7.784695709041462e-03, 3.224671290700398e-01, 2.445134137142996e+00, 3.754408661907416e+00 };
        const double plow = 0.02425, phigh = 1 - plow;
        double q, r;
        if (p < plow)
        {
            q = Math.Sqrt(-2 * Math.Log(p));
            return (((((c[0] * q + c[1]) * q + c[2]) * q + c[3]) * q + c[4]) * q + c[5]) /
                   ((((d[0] * q + d[1]) * q + d[2]) * q + d[3]) * q + 1);
        }
        if (p > phigh)
        {
            q = Math.Sqrt(-2 * Math.Log(1 - p));
            return -(((((c[0] * q + c[1]) * q + c[2]) * q + c[3]) * q + c[4]) * q + c[5]) /
                    ((((d[0] * q + d[1]) * q + d[2]) * q + d[3]) * q + 1);
        }
        q = p - 0.5; r = q * q;
        return (((((a[0] * r + a[1]) * r + a[2]) * r + a[3]) * r + a[4]) * r + a[5]) * q /
               (((((b[0] * r + b[1]) * r + b[2]) * r + b[3]) * r + b[4]) * r + 1);
    }
}
