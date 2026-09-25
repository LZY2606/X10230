using System.Text.Json.Serialization;

namespace Chrono.Core.Domain;

/// <summary>
/// 锚点年龄分布。派生模型必须传播完整分布，禁止以均值替代。
/// 所有实现均为不可变，且分位数/抽样确定可复算。
/// </summary>
[JsonPolymorphic(TypeDiscriminatorPropertyName = "kind")]
[JsonDerivedType(typeof(NormalAge), "normal")]
[JsonDerivedType(typeof(StudentTAge), "studentt")]
[JsonDerivedType(typeof(MixtureAge), "mixture")]
[JsonDerivedType(typeof(EmpiricalAge), "empirical")]
public abstract record AgeDist
{
    public abstract string Scale { get; init; }
    public abstract double Quantile(double p);
    public abstract double Sample(DeterministicRng rng);
    public double Median() => Quantile(0.5);
    public double Lower95() => Quantile(0.025);
    public double Upper95() => Quantile(0.975);

    public static double PhiInv(double p)
    {
        // Peter Acklam 逆正态近似（最大绝对误差 ~1.15e-9）
        if (p <= 0) return double.NegativeInfinity;
        if (p >= 1) return double.PositiveInfinity;
        var pl = 0.02425; var ph = 1 - pl;
        double q, r;
        if (p < pl)
        {
            q = Math.Sqrt(-2 * Math.Log(p));
            return (((((-7.784894002430293e-3 * q - 0.3223964580411365) * q - 2.400758277161838) * q - 2.549732539343734) * q + 4.374664141464968) * q + 2.938163982698783) /
                   ((((7.784695709041462e-3 * q + 0.3224671290700398) * q + 2.445134137142996) * q + 3.754408661907416) * q + 1.0);
        }
        if (p > ph)
        {
            q = Math.Sqrt(-2 * Math.Log(1 - p));
            return -(((((-7.784894002430293e-3 * q - 0.3223964580411365) * q - 2.400758277161838) * q - 2.549732539343734) * q + 4.374664141464968) * q + 2.938163982698783) /
                    ((((7.784695709041462e-3 * q + 0.3224671290700398) * q + 2.445134137142996) * q + 3.754408661907416) * q + 1.0);
        }
        q = p - 0.5; r = q * q;
        return (((((-39.69683028665376 * r + 220.9460984245205) * r - 275.9285104469687) * r + 138.3577518672690) * r - 30.66479806614716) * r + 2.506628277459239) * q /
               (((((-54.47609879822406 * r + 161.5858368580409) * r - 155.6989798598866) * r + 66.80131188771972) * r - 13.28068155288572) * r + 1.0);
    }

    public static double Phi(double x)
    {
        var s = x < 0 ? -1.0 : 1.0;
        var ax = Math.Abs(x) / Math.Sqrt(2);
        var t = 1.0 / (1.0 + 0.3275911 * ax);
        var y = 1.0 - (((((1.061405429 * t - 1.453152027) * t) + 1.421413741) * t - 0.284496736) * t + 0.254829592) * t * Math.Exp(-ax * ax);
        return 0.5 * (1.0 + s * y);
    }

    /// <summary>t 分布分位数（自由度 df），正态 df→∞ 的一般化。</summary>
    public static double TInv(double p, double df)
    {
        if (df >= 1e6) return PhiInv(p);
        if (Math.Abs(p - 0.5) < 1e-15) return 0;
        var tail = p < 0.5;
        var a = tail ? p : 1 - p;
        var z = PhiInv(a);
        // Cornish-Fisher 展开，对 df>=3 精度充足，足以支持速率/不确定度带复核
        var g1 = (z * z + 1) / (4 * df);
        var g2 = (5 * z * z * z + 16 * z + 3) / (96 * df * df);
        var g3 = (3 * z * z * z * z + 19 * z * z + 17 * z * z - 15) / (384 * df * df * df);
        var t = z + g1 * z + g2 * z + g3 * z;
        return tail ? -t : t;
    }
}

public sealed record NormalAge(double Mean, double Sigma, string Scale = "cal BP") : AgeDist
{
    public override string Scale { get; init; } = Scale;
    public override double Quantile(double p) => Mean + Sigma * PhiInv(p);
    public override double Sample(DeterministicRng rng)
    {
        double u1 = 1 - rng.NextDouble();
        double u2 = rng.NextDouble();
        var z = Math.Sqrt(-2 * Math.Log(u1)) * Math.Cos(2 * Math.PI * u2);
        return Mean + Sigma * z;
    }
}

public sealed record StudentTAge(double Location, double Scale_, double Df, string Scale = "cal BP") : AgeDist
{
    public override string Scale { get; init; } = Scale;
    public override double Quantile(double p) => Location + Scale_ * TInv(p, Df);
    public override double Sample(DeterministicRng rng)
    {
        var z = new NormalAge(0, 1).Sample(rng);
        var v = ChiSquaredSample(Df, rng);
        return Location + Scale_ * z * Math.Sqrt(Df / v);
    }
    private static double ChiSquaredSample(double df, DeterministicRng rng)
    {
        // Marsaglia-Tsang gamma(df/2, 2)
        var d = (df - 1.0 / 3.0) / 2.0;
        var c = 1.0 / Math.Sqrt(9.0 * d);
        while (true)
        {
            var x = new NormalAge(0, 1).Sample(rng);
            var v = Math.Pow(1 + c * x, 3);
            var u = rng.NextDouble();
            if (v > 0 && Math.Log(u) < 0.5 * x * x + d * (1 - v + Math.Log(v)))
                return 2 * d * v;
        }
    }
}

public sealed record MixtureAge(IReadOnlyList<MixtureComponent> Components, string Scale = "cal BP") : AgeDist
{
    public override string Scale { get; init; } = Scale;
    public MixtureAge NormalizeWeights()
    {
        var sum = Components.Sum(c => c.Weight);
        return this with { Components = Components.Select(c => c with { Weight = c.Weight / sum }).ToList() };
    }
    public override double Quantile(double p)
    {
        // 混合分布 CDF 单调求根（分位数不能由组件均值混合得到）
        var lo = Components.Min(c => c.Dist.Lower95()) - 2000;
        var hi = Components.Max(c => c.Dist.Upper95()) + 2000;
        for (var i = 0; i < 80; i++)
        {
            var mid = (lo + hi) / 2;
            var cdf = Components.Sum(c => c.Weight * CdfOf(c.Dist, mid));
            if (cdf < p) lo = mid; else hi = mid;
        }
        return (lo + hi) / 2;
    }
    public override double Sample(DeterministicRng rng)
    {
        var u = rng.NextDouble();
        var acc = 0.0;
        foreach (var c in Components)
        {
            acc += c.Weight;
            if (u <= acc) return c.Dist.Sample(rng);
        }
        return Components[^1].Dist.Sample(rng);
    }
    private static double CdfOf(AgeDist d, double x) => d switch
    {
        NormalAge n => Phi((x - n.Mean) / n.Sigma),
        StudentTAge t => Phi((x - t.Location) / (t.Scale_ * Math.Sqrt(t.Df / (t.Df - 2)))),
        MixtureAge m => m.Components.Sum(c => c.Weight * CdfOf(c.Dist, x)),
        EmpiricalAge e => e.Cdf(x),
        _ => throw new NotSupportedException(d.GetType().Name),
    };
}

public sealed record MixtureComponent(double Weight, AgeDist Dist);

public sealed record EmpiricalAge(IReadOnlyList<double> SortedSamples, string Scale = "cal BP") : AgeDist
{
    public override string Scale { get; init; } = Scale;
    public double Cdf(double x)
    {
        int n = SortedSamples.Count, lo = 0, hi = n;
        while (lo < hi) { var mid = (lo + hi) / 2; if (SortedSamples[mid] < x) lo = mid + 1; else hi = mid; }
        return (double)lo / n;
    }
    public override double Quantile(double p)
    {
        var s = SortedSamples;
        var idx = Math.Clamp((int)Math.Round(p * (s.Count - 1)), 0, s.Count - 1);
        return s[idx];
    }
    public override double Sample(DeterministicRng rng)
    {
        var idx = (int)(rng.NextDouble() * SortedSamples.Count);
        return SortedSamples[Math.Min(idx, SortedSamples.Count - 1)];
    }
}

/// <summary>确定性 SplitMix64：等价输入永远产生等价样本序列（种子由稳定身份派生）。</summary>
public sealed class DeterministicRng
{
    private ulong _state;
    public DeterministicRng(ulong seed) => _state = seed;
    public ulong NextUInt64()
    {
        _state += 0x9E3779B97F4A7C15UL;
        var z = _state;
        z = (z ^ (z >> 30)) * 0xBF58476D1CE4E5B9UL;
        z = (z ^ (z >> 27)) * 0x94D049BB133111EBUL;
        return z ^ (z >> 31);
    }
    public double NextDouble() => (NextUInt64() >> 11) * (1.0 / 9007199254740992.0);
}
