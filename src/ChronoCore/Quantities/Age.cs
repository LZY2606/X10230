using System.Globalization;

namespace ChronoCore.Quantities;

/// <summary>时间基准。不同基准的年龄不得直接比较，须经显式校正。</summary>
public enum TimeBasis
{
    Unspecified = 0,
    RadiocarbonBP = 1,   // 14C 常规年龄，BP=1950
    CalBP = 2,           // 日历年龄（距 1950）
    BCECE = 3,           // 公元前/公元
}

public enum DistributionKind { Normal = 0, Uniform = 1, LogNormalLongTail = 2 }

/// <summary>
/// 锚点的放射性年龄分布。推导中只允许使用分位数/抽样，禁止以均值替代分布。
/// 所有实现均可确定性复现（无随机数）。
/// </summary>
public abstract record AgeDistribution
{
    public abstract DistributionKind Kind { get; }
    public abstract TimeBasis Basis { get; }
    public abstract double Quantile(double p);
    public abstract double Mean { get; }
    public abstract double Median { get; }

    /// <summary>确定性等距分位抽样，用于排序概率、不确定度带。</summary>
    public double[] DeterministicSamples(int n)
    {
        var s = new double[n];
        for (int i = 0; i < n; i++)
            s[i] = Quantile((i + 0.5) / n);
        return s;
    }

    public abstract string CanonicalSpec();
}

/// <summary>对称正态年龄分布（单位：年）。</summary>
public sealed record NormalAge(double Mu, double Sigma, TimeBasis Basis) : AgeDistribution
{
    public override DistributionKind Kind => DistributionKind.Normal;
    public override double Mean => Mu;
    public override double Median => Mu;
    public override double Quantile(double p) => Mu + Sigma * Probability.InverseStandardNormal(p);
    public override string CanonicalSpec() =>
        "N(" + Mu.ToString("G12", CultureInfo.InvariantCulture) + "," +
        Sigma.ToString("G12", CultureInfo.InvariantCulture) + "," + Basis + ")";
}

/// <summary>均匀年龄区间（用于只给出上下界的事件约束）。</summary>
public sealed record UniformAge(double Low, double High, TimeBasis Basis) : AgeDistribution
{
    public override DistributionKind Kind => DistributionKind.Uniform;
    public override double Mean => (Low + High) / 2.0;
    public override double Median => Mean;
    public override double Quantile(double p)
    {
        if (p is < 0 or > 1) throw new ArgumentOutOfRangeException(nameof(p));
        return Low + (High - Low) * p;
    }
    public override string CanonicalSpec() =>
        "U(" + Low.ToString("G12", CultureInfo.InvariantCulture) + "," +
        High.ToString("G12", CultureInfo.InvariantCulture) + "," + Basis + ")";
}

/// <summary>
/// 对数正态长尾年龄分布。中位数为 MedianYears，形状参数 SigmaLog 决定长尾。
/// 用中位数（而非均值）表达“中心”，因为均值会被长卡拉高。
/// </summary>
public sealed record LogNormalLongTailAge(double MedianYears, double SigmaLog, double FloorYears, TimeBasis Basis) : AgeDistribution
{
    public override DistributionKind Kind => DistributionKind.LogNormalLongTail;
    public override double Median => MedianYears;
    public override double Mean => FloorYears + MedianYears * Math.Exp(SigmaLog * SigmaLog / 2.0);
    public override double Quantile(double p)
    {
        if (p is < 0 or > 1) throw new ArgumentOutOfRangeException(nameof(p));
        var z = Probability.InverseStandardNormal(p);
        return FloorYears + MedianYears * Math.Exp(SigmaLog * z);
    }
    public override string CanonicalSpec() =>
        "LN(" + MedianYears.ToString("G12", CultureInfo.InvariantCulture) + "," +
        SigmaLog.ToString("G12", CultureInfo.InvariantCulture) + "," +
        FloorYears.ToString("G12", CultureInfo.InvariantCulture) + "," + Basis + ")";
}

/// <summary>储层偏移（年），正值表示样品表观年龄偏老；携带适用水域与规则版本。</summary>
public sealed record ReservoirOffset(
    string Id,
    string ReservoirName,
    double DeltaRYears,
    double UncertaintyYears,
    string RuleVersion,
    string SourcePath);

internal static class Probability
{
    /// <summary>Acklam 有理逼近的标准正态分位函数，绝对误差约 1.15e-9。</summary>
    public static double InverseStandardNormal(double p)
    {
        if (p is <= 0.0 or >= 1.0)
            throw new ArgumentOutOfRangeException(nameof(p), "分位概率必须在 (0,1) 开区间");
        static double Rational(double t, double[] c, double d)
        {
            double num = c[^1], den = d;
            for (int i = c.Length - 2; i >= 0; i--) num = num * t + c[i];
            return num;
        }
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
        const double plow = 0.02425, phigh = 1 - plow;

        if (p < plow)
        {
            double q = Math.Sqrt(-2 * Math.Log(p));
            return (((((c1 * q + c2) * q + c3) * q + c4) * q + c5) * q + c6) /
                   ((((d1 * q + d2) * q + d3) * q + d4) * q + 1);
        }
        if (p <= phigh)
        {
            double q = p - 0.5, r = q * q;
            return (((((a1 * r + a2) * r + a3) * r + a4) * r + a5) * r + a6) * q /
                   (((((b1 * r + b2) * r + b3) * r + b4) * r + b5) * r + 1);
        }
        {
            double q = Math.Sqrt(-2 * Math.Log(1 - p));
            return -(((((c1 * q + c2) * q + c3) * q + c4) * q + c5) * q + c6) /
                    ((((d1 * q + d2) * q + d3) * q + d4) * q + 1);
        }
    }
}
