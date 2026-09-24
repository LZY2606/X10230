using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace CoreChrono.Domain;

/// <summary>
/// A probability distribution over calendar age (canonical unit: cal BP years).
/// Distributions are first-class: an anchor is never replaced by its mean.
/// </summary>
[JsonPolymorphic(TypeDiscriminatorPropertyName = "kind")]
[JsonDerivedType(typeof(NormalDist), "normal")]
[JsonDerivedType(typeof(StudentTDist), "studentt")]
[JsonDerivedType(typeof(MixtureDist), "mixture")]
[JsonDerivedType(typeof(EmpiricalDist), "empirical")]
public abstract class Distribution
{
    public abstract string Kind { get; }
    public abstract double Quantile(double p, ulong seed);
    public abstract double Mean();
    public abstract double StdDev();
    public abstract string CanonicalSpec();

    /// <summary>Lower/upper tail quantiles (default 2.5%/97.5%). Never derived from mean +/- sd for skewed shapes.</summary>
    public (double lo, double hi) CredibleInterval(ulong seed, double plo = 0.025, double phi = 0.975)
        => (Quantile(plo, seed), Quantile(phi, seed));

    public double[] Draw(int n, ulong seed)
    {
        var u = new double[n];
        for (int i = 0; i < n; i++)
            u[i] = Sample(Rng.Split(seed, (ulong)i));
        Array.Sort(u);
        return u;
    }

    protected abstract double Sample(ulong seed);

    protected static double StandardNormal(ulong s)
    {
        // Box-Muller from two deterministic uniforms.
        var rng = new Rng(s);
        double u1 = 1.0 - rng.NextDouble();
        double u2 = 1.0 - rng.NextDouble();
        return Math.Sqrt(-2.0 * Math.Log(u1)) * Math.Cos(2.0 * Math.PI * u2);
    }

    protected static double StudentT(double df, ulong s)
    {
        var z = StandardNormal(s);
        // chi-square via Marsaglia for df degrees of freedom (integer df in our domain).
        int k = Math.Max(1, (int)Math.Round(df));
        var rng = new Rng(Rng.Split(s, 99));
        double sum = 0;
        for (int i = 0; i < k; i++)
        {
            double u1 = 1.0 - rng.NextDouble();
            double u2 = 1.0 - rng.NextDouble();
            double n = Math.Sqrt(-2.0 * Math.Log(u1)) * Math.Cos(2.0 * Math.PI * u2);
            sum += n * n;
        }
        return z / Math.Sqrt(sum / k);
    }

    public static Distribution? Parse(JsonElement el, out string? error)
    {
        error = null;
        try
        {
            var d = el.Deserialize<Distribution>(JsonOptions.Domain);
            if (d is null) error = "分布为空";
            return d;
        }
        catch (Exception ex)
        {
            error = ex.Message;
            return null;
        }
    }
}

public sealed class NormalDist : Distribution
{
    public override string Kind => "normal";
    public double Mu { get; set; }
    public double Sigma { get; set; }
    public NormalDist() { }
    public NormalDist(double mu, double sigma) { Mu = mu; Sigma = sigma; }

    public override double Mean() => Mu;
    public override double StdDev() => Sigma;
    public override string CanonicalSpec() => $"normal(mu={Mu.ToString(CultureInfo.InvariantCulture)},sigma={Sigma.ToString(CultureInfo.InvariantCulture)})";
    protected override double Sample(ulong seed) => Mu + Sigma * StandardNormal(seed);
    public override double Quantile(double p, ulong seed) => Mu + Sigma * Probit(p);

    internal static double Probit(double p)
    {
        // Peter Acklam inverse normal CDF.
        p = Math.Clamp(p, 1e-12, 1 - 1e-12);
        double[] a = { -3.969683028665376e+01, 2.209460984245205e+02, -2.759285104469687e+02, 1.383577518672690e+02, -3.066479806614716e+01, 2.506628277459239e+00 };
        double[] b = { -5.447609879822406e+01, 1.615858368580409e+02, -1.556989798598866e+02, 6.680131188771972e+01, -1.328068155288572e+01 };
        double[] c = { -7.784894002430293e-03, -3.223964580411365e-01, -2.400758277161838e+00, -2.549732539343734e+00, 4.374664141464968e+00, 2.938163982698783e+00 };
        double[] dd = { 7.784695709041462e-03, 3.224671290700398e-01, 2.445134137142996e+00, 3.754408661907416e+00 };
        double plow = 0.02425, phigh = 1 - plow, q, r;
        if (p < plow) { q = Math.Sqrt(-2 * Math.Log(p)); return (((((c[0] * q + c[1]) * q + c[2]) * q + c[3]) * q + c[4]) * q + c[5]) / ((((dd[0] * q + dd[1]) * q + dd[2]) * q + dd[3]) * q + 1); }
        if (p <= phigh) { q = p - 0.5; r = q * q; return (((((a[0] * r + a[1]) * r + a[2]) * r + a[3]) * r + a[4]) * r + a[5]) * q / (((((b[0] * r + b[1]) * r + b[2]) * r + b[3]) * r + b[4]) * r + 1); }
        q = Math.Sqrt(-2 * Math.Log(1 - p));
        return -(((((c[0] * q + c[1]) * q + c[2]) * q + c[3]) * q + c[4]) * q + c[5]) / ((((dd[0] * q + dd[1]) * q + dd[2]) * q + dd[3]) * q + 1);
    }
}
