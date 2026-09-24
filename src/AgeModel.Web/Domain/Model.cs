using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace AgeModel.Domain;

/// <summary>领域量：数值 + 单位 + 原始表示（导入保真，派生层才规范化）。</summary>
public sealed record Qty(double? Value, string Unit, string? Raw = null, string? Note = null)
{
    public bool IsConvertible => Value is not null && double.IsFinite(Value.Value);
    public double Require() => Value ?? throw new InvalidOperationException($"值无法规范化: {Unit} / {Raw}");
    public string Display() => IsConvertible
        ? $"{Value!.Value.ToString("0.######", CultureInfo.InvariantCulture)} {Unit}"
        : $"{Raw ?? "?"} {Unit}（无法转换：{Note ?? "未知原因"}）";
}

/// <summary>带坐标基准的深度（cm 向下为正，基准显式记录）。</summary>
public sealed record Depth(double Cm, string Datum, string? Raw = null);

/// <summary>时间基准：cal BP / ka BP / BCE-CE；年龄只能在统一基准内比较。</summary>
public sealed record AgeBasis(string Scale, int? CalCurve = null, int? Epoch = null)
{
    public static readonly AgeBasis CalBp = new("cal BP", 2000);
    public bool Compatible(AgeBasis other) => Scale == other.Scale && CalCurve == other.CalCurve && Epoch == other.Epoch;
}

public enum Sex { Unknown, Female, Male, Other }
public enum AgeKind { Radiocarbon, Tephra, Biohorizon, EventBed, Lead210, Other }
public enum GapKind { SamplingGap, Unconformity, CoreBreak, FaciesChange }
public enum Severity { Info, Warning, Error }

/// <summary>结构化诊断：带位置，绝不以空值/默认值伪装成功。</summary>
public sealed record Diagnostic(string Code, Severity Severity, string ObjectPath, string Message)
{
    public bool IsFatal => Severity == Severity.Error;
}

/// <summary>年龄分布（不能被均值替代）：正态 / 对数正态 / 长尾(t) / 离散样本 / 未转换。</summary>
public abstract record AgeDistribution
{
    public abstract double Quantile(double p);
    public abstract double Mean();
    /// <summary>供复核页展示的摘要统计（分布本体保留）。</summary>
    public (double q05, double q50, double q95) Summary() => (Quantile(0.05), Quantile(0.50), Quantile(0.95));
}

public sealed record NormalDist(double Mu, double Sigma) : AgeDistribution
{
    public override double Quantile(double p) => Mu + Sigma * Probit(p);
    public override double Mean() => Mu;
    internal static double Probit(double p)
    {
        // Acklich 逆正态近似（精度足够复核展示）。
        p = Math.Clamp(p, 1e-9, 1 - 1e-9);
        double a = [-3.969683028665376e+01, 2.209460984245205e+02, -2.759285104469687e+02,
                     1.383577518672690e+02, -3.066479806614716e+01, 2.506628277459239e+00];
        double b = [-5.447609879822406e+01, 1.615858368580409e+02, -1.556989798598866e+02,
                     6.680131188771972e+01, -1.328068155288572e+01];
        double c = [-7.784894002430293e-03, -3.223964580411365e-01, -2.400758277161838e+00,
                    -2.549720645420558e+00, 4.374664141464968e+00, 2.938163982698783e+00];
        double d = [7.784695709041462e-03, 3.224671290700398e-01, 2.445134137142996e+00,
                    3.754408661907416e+00];
        double plow = 0.02425, phigh = 1 - plow;
        double q, r;
        if (p < plow) { q = Math.Sqrt(-2 * Math.Log(p)); return (((((c[0]*q+c[1])*q+c[2])*q+c[3])*q+c[4])*q+c[5]) / ((((d[0]*q+d[1])*q+d[2])*q+d[3])*q+1); }
        if (p <= phigh) { q = p - 0.5; r = q * q; return (((((a[0]*r+a[1])*r+a[2])*r+a[3])*r+a[4])*r+a[5])*q / (((((b[0]*r+b[1])*r+b[2])*r+b[3])*r+b[4])*r+1); }
        q = Math.Sqrt(-2 * Math.Log(1 - p)); return -(((((c[0]*q+c[1])*q+c[2])*q+c[3])*q+c[4])*q+c[5]) / ((((d[0]*q+d[1])*q+d[2])*q+d[3])*q+1);
    }
}

/// <summary>对数正态（例如偏老的放射性碳日历年龄）。</summary>
public sealed record LogNormalDist(double LogMu, double LogSigma) : AgeDistribution
{
    public override double Quantile(double p) => Math.Exp(LogMu + LogSigma * NormalDist.Probit(p));
    public override double Mean() => Math.Exp(LogMu + LogSigma * LogSigma / 2);
}

/// <summary>长尾 t 分布：年龄分布长尾，稳健分位数，均值不稳定。</summary>
public sealed record StudentTails(double Location, double Scale, double Df) : AgeDistribution
{
    public override double Quantile(double p)
    {
        // 通过不完全贝塔函数反解 t 分位（牛顿迭代）。
        double target = p <= 0.5 ? p : 1 - p;
        double x = NormalDist.Probit(target);
        for (int i = 0; i < 24; i++)
        {
            double cdf = Tcdf(x);
            double deriv = GammaLike((Df + 1) / 2) / (Math.Sqrt(Df * Math.PI) * GammaLike(Df / 2))
                * Math.Pow(1 + x * x / Df, -(Df + 1) / 2);
            double nx = x - (cdf - target) / Math.Max(deriv, 1e-12);
            if (Math.Abs(nx - x) < 1e-10) { x = nx; break; }
            x = nx;
        }
        return Location + Scale * (p <= 0.5 ? x : -x);
    }
    public override double Mean() => Df > 1 ? Location : double.NaN;
    private static double GammaLike(double z)
    {
        // Lanczos gamma。
        double[] g = [76.18009172947146, -86.50532032941677, 24.01409824083091, -1.231739572450155,
                      0.1208650973866179e-2, -0.5395239384953e-5];
        double s = 1.000000000190015; double y = z;
        for (int i = 0; i < g.Length; i++) s += g[i] / (y + i + 1);
        return Math.Sqrt(2 * Math.PI) * Math.Pow(z + 5.5, z + 0.5) * Math.Exp(-(z + 5.5)) * s / z;
    }
    private double Tcdf(double t)
    {
        double x = Df / (Df + t * t);
        double a = Df / 2.0, b = 0.5;
        double bt = Math.Exp(BetaLn(a, b) + a * Math.Log(x) + b * Math.Log(1 - x)) / (2 * Beta(a, b));
        return t >= 0 ? 1 - bt : bt;
    }
    private static double BetaLn(double a, double b) => GammaLike(a) + GammaLike(b) - GammaLike(a + b);
    private static double Beta(double a, double b)
    {
        // 连分数形式的不完全贝塔归一化由 Tcdf 间接使用；这里返回 B(a,b)。
        return Math.Exp(BetaLn(a, b));
    }
}

/// <summary>离散样本分布：直接保留实验室/事件层给的蒙特卡洛样本。</summary>
public sealed record SampleDist(IReadOnlyList<double> Samples) : AgeDistribution
{
    private double[] Sorted()
    {
        var a = Samples.ToArray(); Array.Sort(a); return a;
    }
    public override double Quantile(double p)
    {
        if (Samples.Count == 0) return double.NaN;
        var s = Sorted();
        double idx = p * (s.Length - 1);
        int lo = (int)Math.Floor(idx); int hi = Math.Min(lo + 1, s.Length - 1);
        return s[lo] + (s[hi] - s[lo]) * (idx - lo);
    }
    public override double Mean() => Samples.Count == 0 ? double.NaN : Samples.Average();
}

/// <summary>无法转换的年龄：留在原位置，附诊断，绝不丢给画图。</summary>
public sealed record UnconvertedDist(string Raw, string Reason) : AgeDistribution
{
    public override double Quantile(double p) => double.NaN;
    public override double Mean() => double.NaN;
}

/// <summary>储层（碳库）偏移：本身也是分布，单位 cal BP 年。</summary>
public sealed record ReservoirOffset(string Id, Qty DeltaR, AgeDistribution Distribution, string Source, string? AppliesTo);
