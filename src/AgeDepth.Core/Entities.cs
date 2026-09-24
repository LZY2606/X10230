namespace AgeDepth.Core;

/// <summary>
/// 放射性年龄分布。绝不被均值替代：samples 原样保留，
/// 长尾分布通过经验分位数进入不确定性带。
/// </summary>
public sealed record AgeDist
{
    public required string Kind { get; init; } // normal | uniform | samples
    public double[]? Samples { get; init; }
    public double? Mean { get; init; }
    public double? Sd { get; init; }
    public double? Min { get; init; }
    public double? Max { get; init; }
    public string Unit { get; init; } = "ka";
    public string? Note { get; init; }

    public bool IsValid => Kind switch
    {
        "normal" => Mean is not null && Sd is > 0,
        "uniform" => Min is not null && Max is not null && Max > Min,
        "samples" => Samples is { Length: >= 2 },
        _ => false
    };

    public double Median => Quantile(0.5);

    public double Quantile(double p) => Kind switch
    {
        "normal" => Mean!.Value + Sd!.Value * NormInv(p),
        "uniform" => Min!.Value + (Max!.Value - Min!.Value) * p,
        "samples" => EmpiricalQuantile(Samples!, p),
        _ => double.NaN
    };

    public double[] Draw(int n, Random rng)
    {
        var result = new double[n];
        for (var i = 0; i < n; i++)
            result[i] = Kind switch
            {
                "normal" => Mean!.Value + Sd!.Value * NextStandardNormal(rng),
                "uniform" => Min!.Value + (Max!.Value - Min!.Value) * rng.NextDouble(),
                "samples" => Samples![rng.Next(Samples.Length)],
                _ => double.NaN
            };
        return result;
    }

    /// <summary>储层偏移：整体平移分布（正值使年龄更老），分布形状不变。</summary>
    public AgeDist Shift(double ka) => this with
    {
        Mean = Mean + ka, Min = Min + ka, Max = Max + ka,
        Samples = Samples?.Select(s => s + ka).ToArray()
    };

    private static double EmpiricalQuantile(double[] samples, double p)
    {
        var sorted = samples.OrderBy(x => x).ToArray();
        var pos = p * (sorted.Length - 1);
        var lo = (int)Math.Floor(pos);
        var hi = Math.Min(lo + 1, sorted.Length - 1);
        return sorted[lo] + (sorted[hi] - sorted[lo]) * (pos - lo);
    }

    private static double NextStandardNormal(Random rng)
    {
        var u1 = 1.0 - rng.NextDouble();
        var u2 = rng.NextDouble();
        return Math.Sqrt(-2.0 * Math.Log(u1)) * Math.Cos(2.0 * Math.PI * u2);
    }

    /// <summary>Acklam 逆正态近似。</summary>
    private static double NormInv(double p)
    {
        if (p <= 0) return -6; if (p >= 1) return 6;
        double[] a = { -3.969683028665376e+01, 2.209460984245205e+02, -2.759285104469687e+02, 1.383577518672690e+02, -3.066479806614716e+01, 2.506628277459239e+00 };
        double[] b = { -5.447609879822406e+01, 1.615858368580409e+02, -1.556989798598866e+02, 6.680131188771972e+01, -1.328068155288572e+01 };
        double[] c = { -7.784894002430293e-03, -3.223964580411365e-01, -2.400758277161838e+00, -2.549732539343734e+00, 4.374664141464968e+00, 2.938163982698783e+00 };
        double[] d = { 7.784695709041462e-03, 3.224671290700398e-01, 2.445134137142996e+00, 3.754408661907416e+00 };
        const double pl = 0.02425, ph = 1 - 0.02425;
        if (p < pl)
        {
            var q = Math.Sqrt(-2 * Math.Log(p));
            return (((((c[0] * q + c[1]) * q + c[2]) * q + c[3]) * q + c[4]) * q + c[5]) /
                   ((((d[0] * q + d[1]) * q + d[2]) * q + d[3]) * q + 1);
        }
        if (p > ph)
        {
            var q = Math.Sqrt(-2 * Math.Log(1 - p));
            return -(((((c[0] * q + c[1]) * q + c[2]) * q + c[3]) * q + c[4]) * q + c[5]) /
                    ((((d[0] * q + d[1]) * q + d[2]) * q + d[3]) * q + 1);
        }
        var qq = p - 0.5; var r = qq * qq;
        return (((((a[0] * r + a[1]) * r + a[2]) * r + a[3]) * r + a[4]) * r + a[5]) * qq /
               (((((b[0] * r + b[1]) * r + b[2]) * r + b[3]) * r + b[4]) * r + 1);
    }
}

public sealed record CoreSection(string Id, string Label, RawQuantity Top, RawQuantity Bottom);

/// <summary>显式边界：gap（分段缺口）/ unconformity（不整合）/ fault。gap 可带 DepthTo 区间。</summary>
public sealed record Boundary(string Id, RawQuantity Depth, string Kind, RawQuantity? DepthTo = null, string? Note = null);

public sealed record AgeAnchor(
    string Id, RawQuantity Depth, AgeDist Age, RawQuantity? ReservoirOffset,
    string Source, string Status = "accepted", string? ExclusionReason = null);

public sealed record EventLayer(string Id, RawQuantity Depth, string CorrelatesTo, string? Note = null);

/// <summary>一次导入的不可变材料包。</summary>
public sealed record MaterialsBundle(
    List<CoreSection> Sections,
    List<Boundary> Boundaries,
    List<AgeAnchor> Anchors,
    List<EventLayer> EventLayers);
