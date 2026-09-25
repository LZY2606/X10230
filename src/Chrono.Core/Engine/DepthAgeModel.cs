using Chrono.Core.Domain;

namespace Chrono.Core.Engine;

/// <summary>相邻锚点间的沉积速率区间（完整分布的分位数，而非均值）。</summary>
public sealed record RateInterval
{
    public required string Id { get; init; }
    public required string FromAnchorId { get; init; }
    public required string ToAnchorId { get; init; }
    public required Depth FromDepth { get; init; }
    public required Depth ToDepth { get; init; }
    public double MedianMPerKyr { get; init; }
    public double Q025MPerKyr { get; init; }
    public double Q975MPerKyr { get; init; }
    public bool Decreasing { get; init; }
}

/// <summary>某个深度查询点的年龄不确定度带（cal BP 年）。</summary>
public readonly record struct AgeBand(double Q025, double Median, double Q975)
{
    public bool Defined => !double.IsNaN(Q025);
}

public sealed record ModelBuildReport(
    IReadOnlyList<DepthAgeModel> Candidates,
    IReadOnlyList<Diagnostic> Diagnostics,
    bool BudgetExhausted,
    int TieGroups,
    int AnchorDrawsUsed);

/// <summary>
/// 一个可辩护的深度-年龄模型候选。多个等优候选必须全部保留。
/// 身份由“方法+所选并列锚点+输入内容哈希+参数版本”决定，可往返保持。
/// </summary>
public sealed record DepthAgeModel
{
    public required string Id { get; init; }
    public required string Method { get; init; }
    public required string ParamsVersion { get; init; }
    public required string InputsHash { get; init; }
    public required IReadOnlyList<string> AnchorIds { get; init; }
    public required IReadOnlyList<string> TieChoices { get; init; }
    public required IReadOnlyList<RateInterval> Rates { get; init; }
    public required IReadOnlyList<double> AnchorDepthM { get; init; }
    /// <summary>n draws × k anchors 的日历年龄样本矩阵（cal BP 年）。</summary>
    public required double[][] AgeDraws { get; init; }
    public required int DrawCount { get; init; }
    public required string? SegmentId { get; init; }
    public bool Valid { get; init; } = true;
    public string InvalidReason { get; init; } = "";
    public bool EqualBest { get; init; } = true;
    public double Score { get; init; }

    public AgeBand BandAt(double depthM, double[] boundariesM)
    {
        // 深度落在缺口/不整合的缺失区间内：无定义，显式失效而非插值
        for (var b = 0; b + 1 < boundariesM.Length; b += 2)
        {
            if (depthM > boundariesM[b] + 1e-9 && depthM < boundariesM[b + 1] - 1e-9)
                return new AgeBand(double.NaN, double.NaN, double.NaN);
        }
        var depths = AnchorDepthM;
        if (depths.Count == 0) return new AgeBand(double.NaN, double.NaN, double.NaN);
        if (depthM <= depths[0]) return BandFromColumn(0);
        if (depthM >= depths[^1]) return BandFromColumn(depths.Count - 1);
        var i = 1;
        while (i < depths.Count && depths[i] < depthM) i++;
        var (d0, d1) = (depths[i - 1], depths[i]);
        var w = (depthM - d0) / (d1 - d0);
        var n = DrawCount;
        var projected = new double[n];
        for (var d = 0; d < n; d++)
            projected[d] = AgeDraws[d][i - 1] + w * (AgeDraws[d][i] - AgeDraws[d][i - 1]);
        return MakeBand(projected);
    }

    public AgeBand AnchorBand(int column) => BandFromColumn(column);

    private AgeBand BandFromColumn(int col)
    {
        var n = DrawCount;
        var vals = new double[n];
        for (var d = 0; d < n; d++) vals[d] = AgeDraws[d][col];
        return MakeBand(vals);
    }

    private static AgeBand MakeBand(double[] vals)
    {
        Array.Sort(vals);
        return new AgeBand(QuantileSorted(vals, 0.025), QuantileSorted(vals, 0.5), QuantileSorted(vals, 0.975));
    }

    public static double QuantileSorted(double[] s, double p)
    {
        var pos = p * (s.Length - 1);
        var lo = (int)Math.Floor(pos);
        var hi = Math.Ceiling(pos);
        if (hi == lo) return s[lo];
        return s[lo] + (s[hi] - s[lo]) * (pos - lo);
    }
}
