namespace Chrono.Core.Model;

/// <summary>
/// A radiometric age is always a distribution, never reduced to a mean. Quantiles and deterministic
/// sampling are the only ways derived models consume it. Long-tailed empirical distributions expose
/// asymmetric bands through <see cref="Quantile"/>.
/// </summary>
public abstract record AgeDistribution(string Kind)
{
    public abstract double Quantile(double p);
    public abstract double Sample(ulong stream);
    public abstract string RawText { get; init; }

    public double Median => Quantile(0.5);
    public double Q05 => Quantile(0.05);
    public double Q95 => Quantile(0.95);

    /// <summary>Mean is exposed only for diagnostics/displays, never substituted for the distribution.</summary>
    public abstract double DiagnosticMean { get; }

    public static double DeterministicUniform01(ulong stream)
    {
        // SplitMix64 -> (0,1). Stable across machines and runtimes.
        ulong z = stream + 0x9E3779B97F4A7C15UL;
        z = (z ^ (z >> 30)) * 0xBF58476D1CE4E5B9UL;
        z = (z ^ (z >> 27)) * 0x94D049BB133111EBUL;
        z ^= z >> 31;
        return (z >> 11) * (1.0 / 9007199254740992.0);
    }

    /// <summary>Inverse normal via Acklam; used for Gaussian and as a copula backbone.</summary>
    public static double InverseNormal(double p)
    {
        const double a1 = -3.969683028665376e+01, a2 = 2.209460984245205e+02, a3 = -2.759285104469687e+02;
        const double a4 = 1.383577518672690e+02, a5 = -3.066479806614716e+01, a6 = 2.506628277459239e+00;
        const double b1 = -5.447609879822406e+01, b2 = 1.615858368580409e+02, b3 = -1.556989798598866e+02;
        const double b4 = 6.680131188771972e+01, b5 = -1.328068155288572e+01;
        const double c1 = -7.784894002430293e-03, c2 = -3.223964580411365e-01, c3 = -2.400758277161838e+00;
        const double c4 = -2.549732539343734e+00, c5 = 4.374664141464968e+00, c6 = 2.938163982698783e+00;
        const double d1 = 7.784695709041462e-03, d2 = 3.224671290700398e-01, d3 = 2.445134137142996e+00;
        const double d4 = 3.754408661907416e+00;
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
}

public record GaussianAge(double MeanKa, double SigmaKa, string RawText) : AgeDistribution("gaussian")
{
    public override double Quantile(double p) => MeanKa + SigmaKa * InverseNormal(p);
    public override double Sample(ulong stream) => Quantile(DeterministicUniform01(stream));
    public override double DiagnosticMean => MeanKa;
}

public sealed record UniformAge(double LowKa, double HighKa, string RawText) : AgeDistribution("uniform")
{
    public override double Quantile(double p) => LowKa + (HighKa - LowKa) * p;
    public override double Sample(ulong stream) => Quantile(DeterministicUniform01(stream));
    public override double DiagnosticMean => (LowKa + HighKa) / 2;
}

/// <summary>
/// Empirical (weighted) posterior — the long-tail case. Quantiles come straight from the weighted
/// samples, so asymmetric tails survive into the uncertainty band.
/// </summary>
public sealed record EmpiricalAge(IReadOnlyList<double> SortedKa, IReadOnlyList<double> Weights, string RawText)
    : AgeDistribution("empirical")
{
    public override double Quantile(double p)
    {
        double total = 0;
        for (int i = 0; i < Weights.Count; i++) total += Weights[i];
        double target = total * Math.Clamp(p, 0, 1);
        double acc = 0;
        for (int i = 0; i < SortedKa.Count; i++)
        {
            acc += Weights[i];
            if (acc >= target) return SortedKa[i];
        }
        return SortedKa[^1];
    }

    public override double Sample(ulong stream)
    {
        double u = DeterministicUniform01(stream);
        double total = 0;
        foreach (var w in Weights) total += w;
        double target = u * total, acc = 0;
        for (int i = 0; i < SortedKa.Count; i++)
        {
            acc += Weights[i];
            if (acc >= target) return SortedKa[i];
        }
        return SortedKa[^1];
    }

    public override double DiagnosticMean
    {
        get
        {
            double total = 0, weighted = 0;
            for (int i = 0; i < SortedKa.Count; i++) { total += Weights[i]; weighted += SortedKa[i] * Weights[i]; }
            return weighted / total;
        }
    }
}
