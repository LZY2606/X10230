
namespace CoreChron.Domain;

// 确定性抽样：同一输入摘要 + 锚点身份 => 同一组样本（重放/等价输入稳定）
public sealed class DistributionSampler
{
    private readonly Random _rng;
    public List<string> Diagnostics { get; } = new();

    public DistributionSampler(string seedMaterial)
    {
        int seed = 0;
        foreach (char c in seedMaterial) seed = (seed * 31 + c) & 0x7FFFFFFF;
        _rng = new Random(seed);
    }

    // 返回每年龄(a BP)样本；返回 null 表示该分布不可参与派生（保留原位+诊断）
    public double[]? Sample(AgeDistribution dist, int draws, string where)
    {
        switch (dist)
        {
            case SummaryAge s:
                Diagnostics.Add($"[{where}] 分布不可换算：{s.Diagnostic}");
                return null;
            case NormalAge n:
                return Normal(n.MeanA, Math.Max(n.SigmaA, 0.0), draws);
            case LogNormalAge ln:
                return Enumerable.Range(0, draws).Select(_ =>
                {
                    double z = Gaussian();
                    return Math.Exp(ln.Mu + ln.Sigma * z);
                }).ToArray();
            case MonteCarloAge mc:
            {
                if (mc.SamplesA.Count == 0)
                {
                    Diagnostics.Add($"[{where}] MC 样本为空");
                    return null;
                }
                // 长尾：保留全部样本；确定性循环重采样到 draws
                var arr = mc.SamplesA;
                return Enumerable.Range(0, draws).Select(i => arr[i % arr.Count]
                        + (i >= arr.Count ? 0.0 : 0.0)).ToArray();
            }
            case UncalibratedRadiocarbonAge:
                Diagnostics.Add($"[{where}] 14C 年龄未经校正，必须配合校正曲线与储层偏移");
                return null;
            default:
                Diagnostics.Add($"[{where}] 未知分布类型");
                return null;
        }
    }

    // 14C：14C BP ~ N(bp, sigma)，减储层偏移后反查校正曲线得 cal BP
    public double[]? SampleC14(UncalibratedRadiocarbonAge c14, CalibrationCurve? curve,
        double reservoirA, int draws, string where)
    {
        if (curve is null || curve.Points.Count < 2)
        {
            Diagnostics.Add($"[{where}] 缺少校正曲线，14C 分布保留为 14C BP，不折算");
            return null;
        }
        var pts = curve.Points.OrderBy(p => p.CalBP).ToList();
        var outSamples = new double[draws];
        int wiggle = 0;
        for (int i = 0; i < draws; i++)
        {
            double measured = c14.Bp14C + Gaussian() * Math.Max(c14.Sigma14C, 0.0);
            double effective = measured - reservoirA;
            outSamples[i] = InvertCalibration(pts, effective, ref wiggle);
        }
        if (wiggle > 0)
            Diagnostics.Add($"[{where}] 校正曲线多解（wiggles {wiggle} 处），取确定性首交点并保留标记");
        return outSamples;
    }

    private static double InvertCalibration(List<CalibrationPoint> pts, double c14val, ref int wiggle)
    {
        // 逐段找交点；多解时记录 wiggle 并取最老（cal BP 最大）交点，确定性
        double? chosen = null;
        for (int i = 0; i < pts.Count - 1; i++)
        {
            var a = pts[i]; var b = pts[i + 1];
            double lo = Math.Min(a.C14BP, b.C14BP), hi = Math.Max(a.C14BP, b.C14BP);
            if (c14val < lo || c14val > hi) continue;
            double span = b.C14BP - a.C14BP;
            double t = span == 0 ? 0.5 : (c14val - a.C14BP) / span;
            double cal = a.CalBP + t * (b.CalBP - a.CalBP);
            if (chosen is null || cal > chosen.Value) { chosen = cal; wiggle++; }
        }
        if (chosen is not null) return chosen.Value;
        // 越界：夹到端点并标记（由调用方诊断越界）
        if (c14val > pts.Max(p => p.C14BP)) return pts.Min(p => p.CalBP) - 1;
        return pts.Max(p => p.CalBP) + 1;
    }

    private double[] Normal(double mean, double sigma, int draws)
    {
        var r = new double[draws];
        for (int i = 0; i < draws; i++) r[i] = mean + Gaussian() * sigma;
        return r;
    }

    private double Gaussian()
    {
        // Box-Muller
        double u1 = 1.0 - _rng.NextDouble();
        double u2 = 1.0 - _rng.NextDouble();
        return Math.Sqrt(-2.0 * Math.Log(u1)) * Math.Cos(2.0 * Math.PI * u2);
    }
}

public static class Stats
{
    public static double Quantile(double[] xs, double p)
    {
        var s = xs.OrderBy(x => x).ToArray();
        double h = (s.Length - 1) * p;
        int lo = (int)Math.Floor(h);
        int hi = (int)Math.Ceiling(h);
        if (lo == hi) return s[lo];
        return s[lo] + (h - lo) * (s[hi] - s[lo]);
    }
}
