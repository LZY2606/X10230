using ChronoCore.Quantities;

namespace ChronoCore.Domain;

/// <summary>深度基准（坐标基准）。不可变材料。</summary>
public sealed record DepthDatum(string Id, string Name, string Convention, string Reference, string SourceRef, int SourceVersion);

/// <summary>岩芯分段（不断层段）。Top/Bottom 为相对 DepthDatum 的规范化长度。</summary>
public sealed record CoreSection(
    string Id, string Name, Measure Top, Measure Bottom, string Lithology, string SourceRef, int SourceVersion)
{
    public bool ContainsDepth(double depthM) => depthM >= Top.Canonical!.Value && depthM <= Bottom.Canonical!.Value;
}

/// <summary>分段缺口 / 不整合：显式边界，年龄模型不得跨边界施加单调约束。</summary>
public sealed record GapBoundary(
    string Id, string Kind, Measure Depth, Measure MissingThickness, string Reason, string SourceRef, int SourceVersion);

/// <summary>
/// 放射性年龄分布（锚点）。分布原样保留，绝不允许用均值替代。
/// Empirical 模式用排序后的年龄样本表示长尾分布。
/// </summary>
public sealed class AgeDistribution
{
    public string Id { get; init; }
    public string MaterialType { get; init; }
    public Measure Depth { get; init; }
    public string DistKind { get; init; }            // normal | empirical
    public double? MeanA { get; init; }
    public double? SigmaA { get; init; }
    public double[] SamplesA { get; init; }         // 排序样本，允许长尾
    public double? QuantileLow { get; init; }       // 默认 0.05
    public double? QuantileHigh { get; init; }      // 默认 0.95
    public string ReservoirOffsetId { get; init; }
    public string LabCode { get; init; }
    public string SourceRef { get; init; }
    public int SourceVersion { get; init; }
    public string ExcludedBy { get; set; }
    public string ExcludeReason { get; set; }

    public bool Excluded => ExcludedBy is not null;

    public double QLow => QuantileLow ?? 0.05;
    public double QHigh => QuantileHigh ?? 0.95;

    public (double low, double high) BoundsA(double reservoirShiftA)
    {
        if (DistKind == "empirical" && SamplesA is { Length: > 0 })
            return (Quantile(SamplesA, QLow) + reservoirShiftA, Quantile(SamplesA, QHigh) + reservoirShiftA);
        return (MeanA!.Value - 1.645 * SigmaA!.Value + reservoirShiftA,
                MeanA!.Value + 1.645 * SigmaA!.Value + reservoirShiftA);
    }

    public (double low, double high) RawBoundsA() => BoundsA(0);

    internal static double Quantile(double[] sortedAsc, double q)
    {
        if (sortedAsc.Length == 1) return sortedAsc[0];
        var pos = q * (sortedAsc.Length - 1);
        var lo = (int)Math.Floor(pos);
        var hi = (int)Math.Ceiling(pos);
        if (lo == hi) return sortedAsc[lo];
        return sortedAsc[lo] + (sortedAsc[hi] - sortedAsc[lo]) * (pos - lo);
    }
}

/// <summary>储层偏移（δR）：年龄分布的修正参数，本身是带不确定度的量。</summary>
public sealed record ReservoirOffset(
    string Id, string Name, Measure Mean, Measure Sigma, string Basis, string SourceRef, int SourceVersion);

/// <summary>事件层（火山灰/同位素偏移等），可与外部年龄相关联。</summary>
public sealed record EventLayer(
    string Id, string Name, Measure Depth, string CorrelatedAgeRaw, double? CorrelatedAgeA,
    double? CorrelatedAgeSigmaA, string CorrelationBasis, string SourceRef, int SourceVersion,
    bool CorrelationAccepted = true, string RejectReason = null);

/// <summary>排除决定：全部保留，即使后来被推翻也不删除。</summary>
public sealed record ExclusionDecision(
    string Id, string TargetId, string TargetKind, string Reason, string DecidedBy,
    string AtEventId, int Seq, bool Reversed = false, string ReversedAtEventId = null, string ReversalReason = null);
