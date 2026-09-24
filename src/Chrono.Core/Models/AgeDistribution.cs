using System.Text.Json.Serialization;
using Chrono.Core.Domain;

namespace Chrono.Core.Models;

/// <summary>A distribution is never replaced by its mean. The full shape
/// (including long tails) is retained; summaries are derived read-only views.</summary>
[JsonPolymorphic(TypeDiscriminatorPropertyName = "shape")]
[JsonDerivedType(typeof(NormalDist), "normal")]
[JsonDerivedType(typeof(MixtureDist), "mixture")]
[JsonDerivedType(typeof(EmpiricalDist), "empirical")]
public abstract record AgeDistribution(string Unit, IReadOnlyList<Diagnostic> Diagnostics)
{
    public abstract double Quantile(double p);
    public abstract double Mean();
    public abstract AgeDistribution ShiftReservoir(double yr, double? sdYr);
    public double Median() => Quantile(0.5);
    public (double lo, double hi) Credible95() => (Quantile(0.025), Quantile(0.975));
    public abstract bool IsLongTail();
}

public sealed record NormalDist(double Mu, double Sigma, string Unit, IReadOnlyList<Diagnostic> Diagnostics) : AgeDistribution(Unit, Diagnostics)
{
    public override double Quantile(double p) => Mu + Sigma * Probit(p);
    public override double Mean() => Mu;
    public override bool IsLongTail() => Sigma > 5000;
    public override AgeDistribution ShiftReservoir(double yr, double? sdYr)
    {
        var s2 = Sigma * Sigma + (sdYr ?? 0) * (sdYr ?? 0);
        return new NormalDist(Mu - yr, Math.Sqrt(s2), Unit, Diagnostics);
    }
    internal static double Probit(double p)
    {
        // Peter Acklam rational approximation, accurate to ~1e-9.
        double[] a = {-3.969683028665376e+01,2.209460984245205e+02,-2.759285104469687e+02,
            1.383577518672690e+02,-3.066479806614716e+01,2.506628277459239e+00};
        double[] b = {-5.447609879822406e+01,1.615858368580409e+02,-1.556989798598866e+02,
            6.680131188771972e+01,-1.328068155288572e+01};
        double[] c = {-7.784894002430293e-03,-3.223964580411365e-01,-2.400758277161838e+00,
            -2.549732539343734e+00,4.374664141464968e+00,2.938163982698783e+00};
        double[] d = {7.784695709041462e-03,3.224671290700398e-01,2.445134137142996e+00,
            3.754408661907416e+00};
        const double plow = 0.02425, phigh = 1 - plow;
        double q, r;
        if (p < plow) { q = Math.Sqrt(-2 * Math.Log(p)); return (((((c[0]*q+c[1])*q+c[2])*q+c[3])*q+c[4])*q+c[5]) /
                ((((d[0]*q+d[1])*q+d[2])*q+d[3])*q+1); }
        if (p <= phigh) { q = p - 0.5; r = q*q;
            return (((((a[0]*r+a[1])*r+a[2])*r+a[3])*r+a[4])*r+a[5])*q /
                   (((((b[0]*r+b[1])*r+b[2])*r+b[3])*r+b[4])*r+1); }
        q = Math.Sqrt(-2 * Math.Log(1 - p));
        return -(((((c[0]*q+c[1])*q+c[2])*q+c[3])*q+c[4])*q+c[5]) /
                ((((d[0]*q+d[1])*q+d[2])*q+d[3])*q+1);
    }
}

/// <summary>Mixture distribution models radiocarbon calibration long tails and
/// reworked older material. The components cannot be summarized by one mean.</summary>
public sealed record MixtureDist(IReadOnlyList<(double Weight, NormalDist Comp)> Components, string Unit, IReadOnlyList<Diagnostic> Diagnostics)
    : AgeDistribution(Unit, Diagnostics)
{
    public override double Mean() => Components.Sum(c => c.Weight * c.Comp.Mu);

    public override double Quantile(double p)
    {
        // Bisection over CDF of the weighted mixture.
        double lo = Components.Min(c => c.Comp.Mu - 8 * c.Comp.Sigma);
        double hi = Components.Max(c => c.Comp.Mu + 8 * c.Comp.Sigma);
        for (int i = 0; i < 60; i++)
        {
            var mid = (lo + hi) / 2;
            if (Cdf(mid) < p) lo = mid; else hi = mid;
        }
        return (lo + hi) / 2;
    }

    private double Cdf(double x)
    {
        double s = 0;
        foreach (var (w, c) in Components)
            s += w * 0.5 * (1 + Erf((x - c.Mu) / (c.Sigma * Math.Sqrt(2))));
        return s;
    }

    public override bool IsLongTail()
    {
        if (Components.Count < 2) return false;
        var spread = Components.Max(c => c.Comp.Mu) - Components.Min(c => c.Comp.Mu);
        var pooled = Components.Sum(c => c.Weight * c.Comp.Sigma);
        return spread > 3 * pooled;
    }

    public override AgeDistribution ShiftReservoir(double yr, double? sdYr)
    {
        var shifted = Components.Select(c => (c.Weight, (NormalDist)c.Comp.ShiftReservoir(yr, sdYr))).ToList();
        return new MixtureDist(shifted, Unit, Diagnostics);
    }

    private static double Erf(double x)
    {
        var sign = x < 0 ? -1 : 1; x = Math.Abs(x);
        var t = 1 / (1 + 0.3275911 * x);
        var y = 1 - (((((1.061405429 * t - 1.453152027) * t) + 1.421413741) * t - 0.284496736) * t + 0.254829592) * t * Math.Exp(-x * x);
        return sign * y;
    }
}

/// <summary>Empirical posterior samples (e.g. imported calibration output).
/// Kept verbatim; quantiles come straight from the sample order statistics.</summary>
public sealed record EmpiricalDist(IReadOnlyList<double> Samples, string Unit, IReadOnlyList<Diagnostic> Diagnostics)
    : AgeDistribution(Unit, Diagnostics)
{
    private double[] Sorted()
    {
        var s = Samples.ToArray(); Array.Sort(s); return s;
    }
    public override double Quantile(double p)
    {
        var s = Sorted();
        var idx = Math.Clamp((int)Math.Round(p * (s.Length - 1)), 0, s.Length - 1);
        return s[idx];
    }
    public override double Mean() => Samples.Average();
    public override bool IsLongTail()
    {
        var s = Sorted();
        var q1 = s[s.Length / 4]; var q3 = s[3 * s.Length / 4];
        var iqr = Math.Max(q3 - q1, 1);
        return s[^1] - q3 > 3 * iqr;
    }
    public override AgeDistribution ShiftReservoir(double yr, double? sdYr)
    {
        var shifted = Samples.Select(v => v - yr).ToList();
        return new EmpiricalDist(shifted, Unit, Diagnostics);
    }
}
