using System.Text.Json.Serialization;

namespace AgeModel.Core.Distributions;

/// <summary>
/// Full probability distribution of an anchor age. A distribution MUST NOT be replaced
/// by its mean: every derivation samples/quantiles the distribution itself.
/// </summary>
[JsonPolymorphic(TypeDiscriminatorPropertyName = "kind")]
[JsonDerivedType(typeof(NormalDistribution), "normal")]
[JsonDerivedType(typeof(StudentTDistribution), "student_t")]
[JsonDerivedType(typeof(MixtureDistribution), "mixture")]
[JsonDerivedType(typeof(EmpiricalDistribution), "empirical")]
public abstract record AgeDistribution
{
    public abstract double Mean { get; }
    public abstract double Quantile(double p);
    public abstract double Sample(DeterministicRng rng);
    public abstract string Describe();

    public double Median => Quantile(0.5);

    public double[] SampleMany(DeterministicRng rng, int n)
    {
        var values = new double[n];
        for (int i = 0; i < n; i++) values[i] = Sample(rng);
        return values;
    }
}

public sealed record NormalDistribution(double Mu, double Sigma) : AgeDistribution
{
    private const double Sqrt2 = 1.4142135623730951;
    public override double Mean => Mu;
    public override double Sample(DeterministicRng rng) => Mu + Sigma * rng.NextNormal();
    public override double Quantile(double p) => Mu + Sigma * NormalInverseCdf(p);
    public override string Describe() => $"N({Fmt(Mu)}, {Fmt(Sigma)})";

    internal static double NormalInverseCdf(double p)
    {
        // Acklam inverse normal
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

    private static string Fmt(double v) => Quantities.UnitCatalog.Format(v);
}
