using System.Text.Json.Serialization;

namespace AgeModel.Domain;

/// <summary>
/// Age uncertainty is always a distribution. Rule R2: an anchor's distribution may
/// never be replaced by its mean anywhere in the pipeline. Means exist for display
/// only and are explicitly labelled as such.
/// </summary>
[JsonPolymorphic(TypeDiscriminatorPropertyName = "kind")]
[JsonDerivedType(typeof(NormalDist), "normal")]
[JsonDerivedType(typeof(UniformDist), "uniform")]
[JsonDerivedType(typeof(LongTailDist), "longtail")]
[JsonDerivedType(typeof(EmpiricalDist), "empirical")]
public abstract record AgeDistribution
{
    public abstract double Cdf(double x);
    public abstract double Mean { get; }
    public abstract double SupportLo { get; }
    public abstract double SupportHi { get; }

    /// <summary>Deterministic quantile via bisection on the CDF.</summary>
    public double Quantile(double p)
    {
        if (p <= 0) return SupportLo;
        if (p >= 1) return SupportHi;
        double lo = SupportLo, hi = SupportHi;
        for (int i = 0; i < 80; i++)
        {
            double mid = 0.5 * (lo + hi);
            if (Cdf(mid) < p) lo = mid; else hi = mid;
        }
        return 0.5 * (lo + hi);
    }

    /// <summary>Deterministic Monte-Carlo draw driven by a seeded stream.</summary>
    public double Sample(Random rng) => Quantile(rng.NextDouble());

    protected static double NormalCdf(double z)
    {
        // Abramowitz-Stegun 26.2 style erf approximation.
        double t = 1.0 / (1.0 + 0.2316419 * Math.Abs(z));
        double poly = t * (0.319381530 + t * (-0.356563782 + t * (1.781477937 + t * (-1.821255978 + t * 1.330274429))));
        double pdf = Math.Exp(-0.5 * z * z) / Math.Sqrt(2.0 * Math.PI);
        double cdf = 1.0 - pdf * poly;
        return z >= 0 ? cdf : 1.0 - cdf;
    }
}

public sealed record NormalDist(double Mu, double Sigma) : AgeDistribution
{
    public override double Cdf(double x) => NormalCdf((x - Mu) / Sigma);
    public override double Mean => Mu;
    public override double SupportLo => Mu - 8 * Sigma;
    public override double SupportHi => Mu + 8 * Sigma;
}

public sealed record UniformDist(double Lo, double Hi) : AgeDistribution
{
    public override double Cdf(double x) => x <= Lo ? 0 : x >= Hi ? 1 : (x - Lo) / (Hi - Lo);
    public override double Mean => 0.5 * (Lo + Hi);
    public override double SupportLo => Lo;
    public override double SupportHi => Hi;
}

/// <summary>
/// Mixture of a normal core and an exponential tail toward older ages — the shape
/// radiocarbon outliers actually take (contamination, reworked carbon). The long
/// tail makes mean-substitution visibly wrong, which is why it is a test case.
/// </summary>
public sealed record LongTailDist(double Mu, double Sigma, double TailWeight, double TailLambda) : AgeDistribution
{
    public override double Cdf(double x)
    {
        double core = NormalCdf((x - Mu) / Sigma);
        double tail = x <= Mu ? 0.0 : 1.0 - Math.Exp(-(x - Mu) / TailLambda);
        return (1.0 - TailWeight) * core + TailWeight * tail;
    }
    public override double Mean => (1.0 - TailWeight) * Mu + TailWeight * (Mu + TailLambda);
    public override double SupportLo => Mu - 8 * Sigma;
    public override double SupportHi => Mu + Math.Max(8 * Sigma, 8 * TailLambda);
}

/// <summary>Raw posterior samples (e.g. from a calibration service), kept as-is.</summary>
public sealed record EmpiricalDist(double[] Samples) : AgeDistribution
{
    public override double Cdf(double x)
    {
        var s = Samples.OrderBy(v => v).ToArray();
        int below = s.Count(v => v <= x);
        return (double)below / s.Length;
    }
    public override double Mean => Samples.Average();
    public override double SupportLo => Samples.Min();
    public override double SupportHi => Samples.Max();
}
