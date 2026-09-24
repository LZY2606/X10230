using System.Text.Json.Serialization;

namespace AgeDepth.Core;

/// <summary>
/// An anchor age distribution. The distribution itself is the source of
/// truth: it is never replaced by its mean. Derived layers may shift/scale it
/// (reservoir offset, unit normalization) via non-persisted wrappers.
/// </summary>
[JsonPolymorphic(TypeDiscriminatorPropertyName = "type")]
[JsonDerivedType(typeof(NormalDist), "normal")]
[JsonDerivedType(typeof(GammaDist), "gamma")]
[JsonDerivedType(typeof(UniformDist), "uniform")]
[JsonDerivedType(typeof(SampleDist), "samples")]
public abstract record AgeDist
{
    public abstract double Quantile(double p);
    public abstract double Mean { get; }
    public double Median => Quantile(0.5);
    public double Lo95 => Quantile(0.025);
    public double Hi95 => Quantile(0.975);
    public virtual string Describe() => GetType().Name;
}

public sealed record NormalDist(double MeanValue, double Sd) : AgeDist
{
    public override double Mean => MeanValue;
    public override double Quantile(double p) => MeanValue + Sd * InverseNormalCdf(p);
    public override string Describe() => $"N({MeanValue:0.###},{Sd:0.###})";

    // Acklam's rational approximation of the inverse standard normal CDF.
    internal static double InverseNormalCdf(double p)
    {
        if (p <= 0) return double.NegativeInfinity;
        if (p >= 1) return double.PositiveInfinity;
        double[] a = { -3.969683028665376e+01, 2.209460984245205e+02, -2.759285104469687e+02, 1.383577518672690e+02, -3.066479806614716e+01, 2.506628277459239e+00 };
        double[] b = { -5.447609879822406e+01, 1.615858368580409e+02, -1.556989798598866e+02, 6.680131188771972e+01, -1.328068155288572e+01 };
        double[] c = { -7.784894002430293e-03, -3.223964580411365e-01, -2.400758277161838e+00, -2.549732539343734e+00, 4.374664141464968e+00, 2.938163982698783e+00 };
        double[] d = { 7.784695709041462e-03, 3.224671290700398e-01, 2.445134137142996e+00, 3.754408661907416e+00 };
        const double plow = 0.02425, phigh = 1 - plow;
        if (p < plow)
        {
            var q = Math.Sqrt(-2 * Math.Log(p));
            return (((((c[0] * q + c[1]) * q + c[2]) * q + c[3]) * q + c[4]) * q + c[5]) /
                   ((((d[0] * q + d[1]) * q + d[2]) * q + d[3]) * q + 1);
        }
        if (p > phigh)
        {
            var q = Math.Sqrt(-2 * Math.Log(1 - p));
            return -(((((c[0] * q + c[1]) * q + c[2]) * q + c[3]) * q + c[4]) * q + c[5]) /
                    ((((d[0] * q + d[1]) * q + d[2]) * q + d[3]) * q + 1);
        }
        var qq = p - 0.5;
        var r = qq * qq;
        return (((((a[0] * r + a[1]) * r + a[2]) * r + a[3]) * r + a[4]) * r + a[5]) * qq /
               (((((b[0] * r + b[1]) * r + b[2]) * r + b[3]) * r + b[4]) * r + 1);
    }
}

/// <summary>Long-tailed age distribution (e.g. calibrated C-14 tails).</summary>
public sealed record GammaDist(double Shape, double Scale) : AgeDist
{
    public override double Mean => Shape * Scale;
    // Wilson-Hilferty approximation: deterministic and adequate for bands.
    public override double Quantile(double p)
    {
        var z = NormalDist.InverseNormalCdf(p);
        var t = 1.0 - 1.0 / (9.0 * Shape) + z / (3.0 * Math.Sqrt(Shape));
        var q = Scale * Shape * t * t * t;
        return Math.Max(0, q);
    }
    public override string Describe() => $"Gamma(k={Shape:0.###},θ={Scale:0.###})";
}

public sealed record UniformDist(double Min, double Max) : AgeDist
{
    public override double Mean => (Min + Max) / 2;
    public override double Quantile(double p) => Min + (Max - Min) * p;
    public override string Describe() => $"U({Min:0.###},{Max:0.###})";
}

/// <summary>Discrete calibration samples (e.g. IntCal posterior draws).</summary>
public sealed record SampleDist(double[] Samples) : AgeDist
{
    public override double Mean => Samples.Length == 0 ? 0 : Samples.Average();
    public override double Quantile(double p)
    {
        if (Samples.Length == 0) return 0;
        var s = Samples.OrderBy(x => x).ToArray();
        var pos = p * (s.Length - 1);
        var lo = (int)Math.Floor(pos);
        var hi = Math.Min(lo + 1, s.Length - 1);
        return s[lo] + (s[hi] - s[lo]) * (pos - lo);
    }
    public override string Describe() => $"samples(n={Samples.Length})";
}

/// <summary>Derived-layer wrapper: unit scaling. Never persisted.</summary>
public sealed record ScaledDist(AgeDist Inner, double Factor) : AgeDist
{
    public override double Mean => Inner.Mean * Factor;
    public override double Quantile(double p) => Inner.Quantile(p) * Factor;
    public override string Describe() => $"{Inner.Describe()}×{Factor}";
}

/// <summary>Derived-layer wrapper: reservoir-offset shift. Never persisted.</summary>
public sealed record ShiftedDist(AgeDist Inner, double ShiftKa) : AgeDist
{
    public override double Mean => Inner.Mean + ShiftKa;
    public override double Quantile(double p) => Inner.Quantile(p) + ShiftKa;
    public override string Describe() => $"{Inner.Describe()}{(ShiftKa >= 0 ? "+" : "")}{ShiftKa:0.###}ka";
}

/// <summary>Derived-layer wrapper: affine transform (unit/datum normalization). Never persisted.</summary>
public sealed record AffineDist(AgeDist Inner, double Factor, double Shift) : AgeDist
{
    public override double Mean => Factor >= 0 ? Inner.Mean * Factor + Shift : Factor * Inner.Mean + Shift;
    public override double Quantile(double p)
        => Factor >= 0 ? Inner.Quantile(p) * Factor + Shift : Inner.Quantile(1 - p) * Factor + Shift;
    public override string Describe() => $"{Inner.Describe()}×{Factor}{(Shift >= 0 ? "+" : "")}{Shift:0.###}";
}
