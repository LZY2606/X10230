namespace DepthAge.Core.Models;

/// <summary>深度基准（如 seafloor / MBSF 零点）。不可变材料。</summary>
public sealed record DepthDatum
{
    public required string Id { get; init; }
    public required string Name { get; init; }
    public required string CoordinateSystem { get; init; } // 例如 "depth below seafloor, downward positive"
    public required string CanonicalUnit { get; init; }   // m
    public double ZeroOffsetToCanonicalM { get; init; }  // 基准零点到规范系的偏移
    public string? SourceRef { get; init; }
}

/// <summary>
/// 岩芯分段。缺口（缺芯/未回收）与不整合/断层都是显式边界，
/// 不允许插值跨越。
/// </summary>
public sealed record CoreSection
{
    public required string Id { get; init; }
    public required string CoreId { get; init; }
    public required double TopDepthM { get; init; }
    public required double BottomDepthM { get; init; }
    public required string BottomBoundary { get; init; } // Conformable | Gap | Unconformity | Fault | Inversion
    public string? BoundaryNote { get; init; }
    public string? SourceRef { get; init; }

    public bool IsContinuous => BottomBoundary == "Conformable";
}

public sealed record DepthPoint
{
    public required string DatumId { get; init; }
    public required Quantity Quantity { get; init; }
}

public enum AgeKind
{
    Calendar,        // 已校准日历年龄（含事件层年龄）
    Radiocarbon,     // 未校准 14C 年龄（需校准曲线 + 储层偏移）
    EventDerived     // 由事件层相关推导
}

/// <summary>年龄分布：完整分布保留，任何派生都不允许退化为单一均值。</summary>
public sealed record AgeDistribution
{
    public required AgeKind Kind { get; init; }
    public required Quantity ReportedAge { get; init; }      // 原始报告值（保留原表示）
    public double? ReportedSigma { get; init; }
    public string? CalendarUnit { get; init; }               // 校准后单位（通常 cal yr BP）
    /// <summary>经验分位数：p -> cal yr BP。长尾由样本/分位数共同表达。</summary>
    public List<QuantilePoint> Quantiles { get; init; } = new();
    /// <summary>校准后经验样本（cal yr BP）；确定性种子重放。</summary>
    public List<double> SamplesCalYrBP { get; init; } = new();
    public bool CalendarReady { get; init; }
    public List<string> Diagnostics { get; init; } = new();
    public string? CalibrationCurveId { get; init; }
    public string? ReservoirOffsetId { get; init; }
}

public sealed record QuantilePoint(double P, double CalYrBP);

public sealed record AnchorMaterial
{
    public required string Id { get; init; }
    public required string Label { get; init; }
    public required string CoreId { get; init; }
    public required DepthPoint Depth { get; init; }
    public required AgeDistribution Age { get; init; }
    public string? EventBedId { get; init; }
    public string? Method { get; init; }      // AMS14C / tephro / biostrat ...
    public string? SourceRef { get; init; }
    public bool Excluded { get; init; }       // 由排除事件决定；材料本身永不删除
    public string? ExclusionId { get; init; }
}

/// <summary>储层偏移（海洋碳库效应等）：先扣减再校准。作用范围显式声明。</summary>
public sealed record ReservoirOffset
{
    public required string Id { get; init; }
    public required string Name { get; init; }
    public required double DeltaRYr { get; init; }
    public required double SigmaYr { get; init; }
    public required double AppliesTopDepthM { get; init; }
    public required double AppliesBottomDepthM { get; init; }
    public string? SourceRef { get; init; }

    public bool AppliesTo(double depthM) => depthM >= AppliesTopDepthM - 1e-9 && depthM <= AppliesBottomDepthM + 1e-9;
}

public sealed record CalibrationPoint(double RadiocarbonYrBP, double CalYrBP, double SigmaCalYr = 0);

/// <summary>校准曲线（教学用可只提供局部段；超出范围给出诊断而非伪造）。</summary>
public sealed record CalibrationCurve
{
    public required string Id { get; init; }
    public required string Name { get; init; }
    public required List<CalibrationPoint> Points { get; init; }
    public string? SourceRef { get; init; }

    public bool TryCalibrate(double radiocarbonYrBP, double sigma14C, int seed, out List<double> samples, out List<QuantilePoint> quantiles, out string? diagnostic)
    {
        samples = new();
        quantiles = new();
        var ordered = Points.OrderBy(p => p.RadiocarbonYrBP).ToList();
        if (radiocarbonYrBP < ordered[0].RadiocarbonYrBP - sigma14C ||
            radiocarbonYrBP > ordered[^1].RadiocarbonYrBP + sigma14C)
        {
            diagnostic = $"14C 年龄 {radiocarbonYrBP}±{sigma14C} 超出校准曲线覆盖范围 " +
                         $"[{ordered[0].RadiocarbonYrBP}, {ordered[^1].RadiocarbonYrBP}]，保留原值不外推";
            return false;
        }

        var rng = new DeterministicRng(seed);
        const int n = 400;
        for (var i = 0; i < n; i++)
        {
            var rc = radiocarbonYrBP + sigma14C * rng.NextNormal();
            var cal = Interpolate(ordered, rc);
            if (cal is { } c)
                samples.Add(c);
        }
        samples.Sort();
        if (samples.Count < 8)
        {
            diagnostic = "校准后有效样本不足（曲线局部缺失），不能形成可用分布";
            return false;
        }
        double[] ps = { 0.025, 0.05, 0.16, 0.25, 0.5, 0.75, 0.84, 0.95, 0.975 };
        foreach (var p in ps)
            quantiles.Add(new QuantilePoint(p, Quantile(samples, p)));
        diagnostic = null;
        return true;
    }

    private static double? Interpolate(List<CalibrationPoint> ordered, double rc)
    {
        if (rc < ordered[0].RadiocarbonYrBP || rc > ordered[^1].RadiocarbonYrBP) return null;
        for (var i = 0; i < ordered.Count - 1; i++)
        {
            var a = ordered[i];
            var b = ordered[i + 1];
            if (rc >= a.RadiocarbonYrBP && rc <= b.RadiocarbonYrBP)
            {
                var t = (rc - a.RadiocarbonYrBP) / (b.RadiocarbonYrBP - a.RadiocarbonYrBP);
                return a.CalYrBP + t * (b.CalYrBP - a.CalYrBP);
            }
        }
        return ordered[^1].CalYrBP;
    }

    public static double Quantile(List<double> sorted, double p)
    {
        var idx = Math.Clamp((int)Math.Round(p * (sorted.Count - 1)), 0, sorted.Count - 1);
        return sorted[idx];
    }
}

/// <summary>确定性正态随机数（Box–Muller，固定种子保证重放）。</summary>
public sealed class DeterministicRng
{
    private readonly Random _random;
    public DeterministicRng(int seed) => _random = new Random(seed);
    public double NextNormal()
    {
        var u1 = 1.0 - _random.NextDouble();
        var u2 = 1.0 - _random.NextDouble();
        return Math.Sqrt(-2.0 * Math.Log(u1)) * Math.Sin(2.0 * Math.PI * u2);
    }
}

public sealed record EventBed
{
    public required string Id { get; init; }
    public required string Name { get; init; }
    public required AgeDistribution Age { get; init; }  // 事件层自身日历年龄分布
    public string? SourceRef { get; init; }
}

/// <summary>事件层相关：把事件层挂到本孔深度；同一事件层的锚点共享年龄分布。</summary>
public sealed record EventCorrelation
{
    public required string Id { get; init; }
    public required string EventBedId { get; init; }
    public required string CoreId { get; init; }
    public required DepthPoint Depth { get; init; }
    public double? DepthToleranceM { get; init; }
    public string? SourceRef { get; init; }
}

/// <summary>排除决定本身也是被保留的材料；被排除的锚点不删除。</summary>
public sealed record ExclusionDecision
{
    public required string Id { get; init; }
    public required string TargetAnchorId { get; init; }
    public required string Reason { get; init; }          // Reworked | Contamination | Duplicate | ManualReview ...
    public required string Basis { get; init; }
    public required string DecidedBy { get; init; }
    public required DateTimeOffset DecidedAt { get; init; }
    public bool Revoked { get; init; }
    public string? RevokeEventId { get; init; }
}
