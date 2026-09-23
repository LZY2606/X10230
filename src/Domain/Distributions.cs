using System.Globalization;

namespace DepthAgeReview.Domain;

/// <summary>
/// 锚点年龄分布。派生层直接消费分布（分位数/抽样/尾重），
/// 任何调用方都不能只取均值。StudentT 显式承载长尾；Empirical 承载校正后的离散分布。
/// </summary>
public abstract record AgeDistribution(AgeDistributionKind Kind)
{
    public abstract double Quantile(double p);
    public abstract double Mean { get; }
    public abstract double StdDev { get; }
    /// <summary>尾重指标：(Q0.975-Q0.5)/(Q0.5-Q0.025) 等不对称度 + 绝对半宽。供“长尾”判定。</summary>
    public double TailRightRatio => (Quantile(0.975) - Quantile(0.5)) / Math.Max(1e-9, Quantile(0.5) - Quantile(0.025));
    public double HalfWidth95 => Quantile(0.975) - Quantile(0.025);

    /// <summary>确定性抽样：给定索引与盐值生成稳定的 [0,1) 序列（多重采样哈希），保证可重放。</summary>
    public double Sample(int drawIndex, string salt)
    {
        unchecked
        {
            var h = 1469598103934665603UL;
            foreach (var c in salt) { h ^= c; h *= 1099511628211UL; }
            h ^= (ulong)drawIndex * 1099511628211UL;
            h ^= (ulong)(int)(Mean * 1000);
            for (var i = 0; i < 4; i++) { h ^= h >> 33; h *= 1469598103934665603UL; }
            return (h >> 11) * (1.0 / 9007199254740992.0);
        }
    }
}

public sealed record NormalDist(double Mu, double Sigma) : AgeDistribution(AgeDistributionKind.Normal)
{
    public override double Mean => Mu;
    public override double StdDev => Sigma;
    public override double Quantile(double p) => Mu + Sigma * NormalCdfInverse(p);

    // Acklam 逆正态 CDF
    internal static double NormalCdfInverse(double p)
    {
        p = Math.Clamp(p, 1e-12, 1 - 1e-12);
        var a = new[] { -3.969683028665376e+01, 2.209460984245205e+02, -2.759285104469687e+02, 1.383577518672690e+02, -3.066479806614716e+01, 2.506628277459239e+00 };
        var b = new[] { -5.447609879822406e+01, 1.615858368580409e+02, -1.556989798598866e+02, 6.680131188771972e+01, -1.328068155288572e+01 };
        var c = new[] { -7.784894002430293e-03, -3.223964580411365e-01, -2.400758277161838e+00, -2.549732539343734e+00, 4.374664141464968e+00, 2.938163982698783e+00 };
        var d = new[] { 7.784695709041462e-03, 3.224671290700398e-01, 2.445134137142996e+00, 3.754408661907416e+00 };
        var plow = 0.02425;
        var phigh = 1 - plow;
        double q, r;
        if (p < plow)
        {
            q = Math.Sqrt(-2 * Math.Log(p));
            return (((((c[0] * q + c[1]) * q + c[2]) * q + c[3]) * q + c[4]) * q + c[5]) /
                   ((((d[0] * q + d[1]) * q + d[2]) * q + d[3]) * q + 1);
        }
        if (p <= phigh)
        {
            q = p - 0.5; r = q * q;
            return (((((a[0] * r + a[1]) * r + a[2]) * r + a[3]) * r + a[4]) * r + a[5]) * q /
                   (((((b[0] * r + b[1]) * r + b[2]) * r + b[3]) * r + b[4]) * r + 1);
        }
        q = Math.Sqrt(-2 * Math.Log(1 - p));
        return -(((((c[0] * q + c[1]) * q + c[2]) * q + c[3]) * q + c[4]) * q + c[5]) /
                ((((d[0] * q + d[1]) * q + d[2]) * q + d[3]) * q + 1);
    }
}

/// <summary>Student-t：ν 越小尾越长。ν≥30 近似正态。承载“年龄分布长尾”。</summary>
public sealed record StudentTDist(double Mu, double Scale, double Nu) : AgeDistribution(AgeDistributionKind.StudentT)
{
    public override double Mean => Nu > 1 ? Mu : double.NaN;
    public override double StdDev => Nu > 2 ? Scale * Math.Sqrt(Nu / (Nu - 2)) : double.PositiveInfinity;
    public override double Quantile(double p) => Mu + Scale * TInv(p, Nu);

    internal static double TInv(double p, double nu)
    {
        // Student-t 逆 CDF：用对 beta 分布的关系 + 对逆 beta 的牛顿迭代
        var x = NormalDist.NormalCdfInverse(p);
        var g1 = (x * x * x + x) / 4;
        var g2 = (5 * Math.Pow(x, 5) + 16 * Math.Pow(x, 3) + 3 * x) / 96;
        var g3 = (3 * Math.Pow(x, 7) + 19 * Math.Pow(x, 5) + 17 * Math.Pow(x, 3) - 15 * x) / 384;
        return x + g1 / nu + g2 / (nu * nu) + g3 / (nu * nu * nu);
    }
}

public sealed record PointDist(double Value) : AgeDistribution(AgeDistributionKind.Point)
{
    public override double Mean => Value;
    public override double StdDev => 0;
    public override double Quantile(double p) => Value;
}

/// <summary>经验（校正后）分布：确定性排序样本，分位数线性插值；不强制对称。</summary>
public sealed record EmpiricalDist(IReadOnlyList<double> Samples) : AgeDistribution(AgeDistributionKind.Empirical)
{
    private readonly double[] _sorted = Samples.OrderBy(v => v).ToArray();
    public override double Mean => _sorted.Average();
    public override double StdDev
    {
        get
        {
            var m = Mean;
            return Math.Sqrt(_sorted.Sum(v => (v - m) * (v - m)) / _sorted.Length);
        }
    }
    public override double Quantile(double p)
    {
        p = Math.Clamp(p, 0, 1);
        var n = _sorted.Length;
        if (n == 1) return _sorted[0];
        var idx = p * (n - 1);
        var lo = (int)Math.Floor(idx);
        var hi = Math.Min(n - 1, lo + 1);
        var frac = idx - lo;
        return _sorted[lo] * (1 - frac) + _sorted[hi] * frac;
    }
    public IReadOnlyList<double> Sorted => _sorted;

    public static EmpiricalDist ParseCsv(string raw, CultureInfo? culture = null)
    {
        var vals = raw.Split(',', ';', ' ', '\t', '\n', '\r')
            .Where(s => !string.IsNullOrWhiteSpace(s))
            .Select(s => double.Parse(s.Trim(), CultureInfo.InvariantCulture)).ToArray();
        return new EmpiricalDist(vals);
    }
}
