namespace Chrono.Core.Model;

/// <summary>
/// Every domain quantity carries the raw representation from import plus a normalized
/// value used only by derived layers. Values that cannot be converted stay raw and
/// receive a diagnostic; they are never dropped to make plots work.
/// </summary>
public sealed record Measure(string Raw, double? Value, string CanonicalUnit, string? CoordinateDatum, string? ErrorCode, string? ErrorDetail)
{
    public bool IsConvertible => Value.HasValue && ErrorCode is null;
}

public enum Severity { Error, Warning, Info }

public sealed record Diagnostic(string Code, string Message, string? MaterialId, string? Field, string? SourceFragment)
{
    public Severity Severity { get; init; } = Severity.Error;
}

/// <summary>Age probability distribution. A distribution is never replaced by its mean.</summary>
public abstract record AgeDistribution(string Kind)
{
    public abstract double MeanCalendarYearBP { get; }
    public abstract double Quantile(double p);
    public abstract double Sample(System.Random rng);
}

/// <summary>Symmetric Gaussian in calendar years BP; sigma in years.</summary>
public sealed record GaussianAge(double Mean, double Sigma) : AgeDistribution("gaussian")
{
    public override double MeanCalendarYearBP => Mean;
    public override double Quantile(double p)
    {
        // inverse standard normal via rational approximation (Abramowitz & Stegun 26.2.17)
        var z = NormalInverse(p);
        return Mean + Sigma * z;
    }
    public override double Sample(System.Random rng)
    {
        double u1 = 1.0 - rng.NextDouble();
        double u2 = 1.0 - rng.NextDouble();
        double z = Math.Sqrt(-2.0 * Math.Log(u1)) * Math.Cos(2.0 * Math.PI * u2);
        return Mean + Sigma * z;
    }

    internal static double NormalInverse(double p)
    {
        if (p <= 0) return double.NegativeInfinity;
        if (p >= 1) return double.PositiveInfinity;
        const double b0 = 0.2316419, b1 = 0.319381530, b2 = -0.356563782,
            b3 = 1.781477937, b4 = -1.821255978, b5 = 1.330274429;
        var x = p > 0.5 ? p : 1.0 - p;
        var t = 1.0 / (1.0 + b0 * Math.Sqrt(-2.0 * Math.Log(x)));
        var poly = ((((b5 * t + b4) * t + b3) * t + b2) * t + b1) * t;
        var z = Math.Sqrt(-2.0 * Math.Log(x)) - poly;
        return p > 0.5 ? z : -z;
    }
}

/// <summary>
/// Long-tail distribution: mixture of a Gaussian core and a wider Gaussian tail.
/// Preserves asymmetric tail behaviour; quantiles/means come from the mixture,
/// never from collapsing to a single number.
/// </summary>
public sealed record LongTailAge(double CoreMean, double CoreSigma, double TailMean, double TailSigma, double TailWeight)
    : AgeDistribution("long_tail_mixture")
{
    public override double MeanCalendarYearBP =>
        (1.0 - TailWeight) * CoreMean + TailWeight * TailMean;

    public override double Quantile(double p)
    {
        // bisection on the mixture CDF — accurate enough for interval derivation.
        double lo = CoreMean - 8 * Math.Max(CoreSigma, TailSigma) - Math.Abs(TailMean);
        double hi = CoreMean + 8 * Math.Max(CoreSigma, TailSigma) + Math.Abs(TailMean);
        for (var i = 0; i < 80; i++)
        {
            var mid = (lo + hi) / 2;
            var cdf = Cdf(mid);
            if (cdf < p) lo = mid; else hi = mid;
        }
        return (lo + hi) / 2;
    }

    private double Cdf(double y)
    {
        double Phi(double z) => 0.5 * (1.0 + Erf(z / Math.Sqrt(2.0)));
        return (1.0 - TailWeight) * Phi((y - CoreMean) / CoreSigma)
             + TailWeight * Phi((y - TailMean) / TailSigma);
    }

    public override double Sample(System.Random rng)
    {
        var useTail = rng.NextDouble() < TailWeight;
        var (mean, sigma) = useTail ? (TailMean, TailSigma) : (CoreMean, CoreSigma);
        double u1 = 1.0 - rng.NextDouble(), u2 = 1.0 - rng.NextDouble();
        double z = Math.Sqrt(-2.0 * Math.Log(u1)) * Math.Cos(2.0 * Math.PI * u2);
        return mean + sigma * z;
    }

    private static double Erf(double x)
    {
        // Abramowitz & Stegun 7.1.26
        var sign = Math.Sign(x);
        x = Math.Abs(x);
        var t = 1.0 / (1.0 + 0.3275911 * x);
        var y = 1.0 - (((((1.061405429 * t - 1.453152027) * t) + 1.421413741) * t - 0.284496736) * t + 0.254829592) * t * Math.Exp(-x * x);
        return sign * y;
    }
}
