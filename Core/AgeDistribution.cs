namespace DepthAge.Core;

/// <summary>
/// 放射性年龄分布（始终携带年龄单位）。
/// 关键规则：分布只能以分布身份参与推导——可以抽样、取分位数，但绝不允许被均值替代。
/// </summary>
public sealed record AgeDistribution(
    string Kind,
    string Unit,
    double? Mean,
    double? Sigma,
    double? Min,
    double? Max,
    double[]? Samples)
{
    public const string Normal = "normal";
    public const string Uniform = "uniform";
    public const string SamplesKind = "samples";

    /// <summary>把整个分布（含长尾样本点）平移/缩放，保持分布身份。</summary>
    public AgeDistribution WithLinearTransform(double factor, double shift, string canonicalUnit) => new(
        Kind, canonicalUnit,
        Mean is { } m ? m * factor + shift : null,
        Sigma is { } s ? s * factor : null,
        Min is { } lo ? lo * factor + shift : null,
        Max is { } hi ? hi * factor + shift : null,
        Samples?.Select(v => v * factor + shift).ToArray());

    public double Quantile(double p)
    {
        p = Math.Clamp(p, 0.0, 1.0);
        return Kind switch
        {
            Normal => Mean!.Value + Sigma!.Value * NormalInverseCdf(p),
            Uniform => Min!.Value + (Max!.Value - Min!.Value) * p,
            SamplesKind => EmpiricalQuantile(p),
            _ => throw new InvalidOperationException($"unknown distribution kind '{Kind}'")
        };
    }

    public double Draw(XorShift rng) => Kind switch
    {
        Normal => Mean!.Value + Sigma!.Value * NormalSample(rng),
        Uniform => Min!.Value + (Max!.Value - Min!.Value) * rng.NextDouble(),
        SamplesKind => Samples![Math.Clamp((int)(rng.NextDouble() * Samples.Length), 0, Samples.Length - 1)],
        _ => throw new InvalidOperationException($"unknown distribution kind '{Kind}'")
    };

    /// <summary>对数密度（去掉公共常数），用于候选评分。</summary>
    public double LogPdf(double x)
    {
        switch (Kind)
        {
            case Normal:
                var z = (x - Mean!.Value) / Sigma!.Value;
                return -0.5 * z * z - Math.Log(Sigma!.Value);
            case Uniform:
                return (x >= Min!.Value && x <= Max!.Value) ? -Math.Log(Max!.Value - Min!.Value) : -1e9;
            case SamplesKind:
            {
                var s = Samples!;
                var mean = s.Average();
                var sd = Math.Sqrt(s.Sum(v => (v - mean) * (v - mean)) / s.Length);
                var h = Math.Max(sd * 0.25, 1e-6);
                double sum = 0;
                foreach (var v in s) sum += Math.Exp(-0.5 * ((x - v) / h) * ((x - v) / h));
                return Math.Log(Math.Max(sum / (s.Length * h), 1e-300));
            }
            default: throw new InvalidOperationException($"unknown distribution kind '{Kind}'");
        }
    }

    /// <summary>样本分布的偏度；绝对偏度 &gt; 1 视为长尾，引擎保留全部样本点。</summary>
    public double? Skewness()
    {
        if (Kind != SamplesKind || Samples is null || Samples.Length < 3) return null;
        var s = Samples;
        var mean = s.Average();
        var m2 = s.Average(v => Math.Pow(v - mean, 2));
        var m3 = s.Average(v => Math.Pow(v - mean, 3));
        if (m2 <= 0) return 0;
        return m3 / Math.Pow(m2, 1.5);
    }

    public bool IsLongTail => Skewness() is { } skew && Math.Abs(skew) > 1.0;

    private double EmpiricalQuantile(double p)
    {
        var sorted = (double[])Samples!.Clone();
        Array.Sort(sorted);
        if (sorted.Length == 1) return sorted[0];
        var pos = p * (sorted.Length - 1);
        var i = (int)Math.Floor(pos);
        var frac = pos - i;
        return i >= sorted.Length - 1 ? sorted[^1] : sorted[i] * (1 - frac) + sorted[i + 1] * frac;
    }

    private static double NormalSample(XorShift rng)
    {
        // Box–Muller
        double u1, u2;
        do { u1 = rng.NextDouble(); } while (u1 <= 1e-12);
        u2 = rng.NextDouble();
        return Math.Sqrt(-2.0 * Math.Log(u1)) * Math.Cos(2.0 * Math.PI * u2);
    }

    /// <summary>Acklam 有理逼近，误差 &lt; 1.15e-9，足以支撑 5%/95% 分位数判定。</summary>
    private static double NormalInverseCdf(double p)
    {
        if (p <= 0) return double.NegativeInfinity;
        if (p >= 1) return double.PositiveInfinity;
        const double a1 = -3.969683028665376e+01, a2 = 2.209460984245205e+02,
            a3 = -2.759285104469687e+02, a4 = 1.383577518672690e+02,
            a5 = -3.066479806614716e+01, a6 = 2.506628277459239e+00;
        const double b1 = -5.447609879822406e+01, b2 = 1.615858368580409e+02,
            b3 = -1.556989798598866e+02, b4 = 6.680131188771972e+01,
            b5 = -1.328068155288572e+01;
        const double c1 = -7.784894002430293e-03, c2 = -3.223964580411365e-01,
            c3 = -2.400758277161838e+00, c4 = -2.549732539343734e+00,
            c5 = 4.374664141464968e+00, c6 = 2.938163982698783e+00;
        const double d1 = 7.784695709041462e-03, d2 = 3.224671290700398e-01,
            d3 = 2.445134137142996e+00, d4 = 3.754408661907416e+00;
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
