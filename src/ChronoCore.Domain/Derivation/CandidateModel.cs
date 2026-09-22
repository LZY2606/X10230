namespace ChronoCore.Domain.Derivation;

public enum DerivationStatus
{
    /// <summary>成功：至少一个合法候选。</summary>
    Ok = 0,
    /// <summary>资源预算耗尽：并列组合数超过上限（区别于格式损坏/语义无解）。</summary>
    BudgetExhausted = 1,
    /// <summary>语义无解：不存在任何合法候选（如不断层段内硬性倒置）。</summary>
    SemanticallyUnsolvable = 2,
    /// <summary>没有足够输入。</summary>
    InsufficientInput = 3
}

/// <summary>候选深度-年龄模型。多个等优候选全部保留，不做唯一化。</summary>
public sealed class CandidateModel
{
    public required string Id { get; init; }
    public required string RuleSetId { get; init; }
    public int RuleVersion { get; init; }
    /// <summary>并列锚点的顺序选择（稳定排列签名）。</summary>
    public required string TieSignature { get; init; }
    public required List<ModelNode> Nodes { get; init; }
    /// <summary>每段的速率区间与单调概率。</summary>
    public required List<IntervalRate> Rates { get; init; }
    /// <summary>不确定度带网格（仅段内锚点跨度内，绝不跨越缺口/不整合）。</summary>
    public required List<UncertaintyBandPoint> Band { get; init; }
    public bool Valid { get; init; }
    public required List<string> InvalidReasons { get; init; }
    /// <summary>分数（越小越优：倒置惩罚 + 不确定度 + 外推）。</summary>
    public double Score { get; init; }
    /// <summary>与其它候选分数不可区分（等优）。</summary>
    public bool Equifinal { get; set; }
    public required IReadOnlyList<string> IncludedAnchorIds { get; init; }
    /// <summary>输入摘要（canonical hash）。</summary>
    public required string InputDigest { get; init; }
    public required IReadOnlyList<string> DependencyNodes { get; init; }
    public required DateTimeOffset DerivedAt { get; init; }

    public BandValue? AgeAt(double depthM)
    {
        var point = Band.Where(b => Math.Abs(b.DepthM - depthM) < 1e-9)
            .OrderBy(b => Math.Abs(b.DepthM - depthM)).FirstOrDefault();
        if (point is not null) return point.ToBandValue();
        // 落在两个网格点之间：线性插值分位数
        for (int i = 0; i < Band.Count - 1; i++)
        {
            var a = Band[i];
            var b = Band[i + 1];
            if (a.SegmentId != b.SegmentId) continue;
            if (depthM >= a.DepthM && depthM <= b.DepthM)
            {
                var t = (depthM - a.DepthM) / (b.DepthM - a.DepthM);
                return new BandValue(
                    Lerp(a.Q025, b.Q025, t), Lerp(a.Median, b.Median, t), Lerp(a.Q975, b.Q975, t),
                    a.SegmentId, false, null);
            }
        }
        var reason = OutsideReason(depthM);
        return new BandValue(null, null, null, "", false, reason);
    }

    private string? OutsideReason(double depthM)
    {
        var anySeg = Nodes.Count > 0;
        if (!anySeg) return "no-anchors";
        return Band.Any(b => b.DepthM <= depthM) && Band.Any(b => b.DepthM >= depthM)
            ? "boundary-crossed"
            : "outside-anchored-span";
    }

    private static double Lerp(double a, double b, double t) => a + (b - a) * t;
}

public sealed record ModelNode
{
    public required string AnchorId { get; init; }
    public required string Label { get; init; }
    public required string SegmentId { get; init; }
    public double DepthM { get; init; }
    public double MedianYearsBp { get; init; }
    public double Q025 { get; init; }
    public double Q975 { get; init; }
    public int OrderInTie { get; init; }
}

public sealed record IntervalRate
{
    public required string SegmentId { get; init; }
    public required string AboveAnchorId { get; init; }
    public required string BelowAnchorId { get; init; }
    public double TopDepthM { get; init; }
    public double BottomDepthM { get; init; }
    /// <summary>速率中位数 cm/yr。</summary>
    public double MedianCmPerYear { get; init; }
    public double Q025CmPerYear { get; init; }
    public double Q975CmPerYear { get; init; }
    /// <summary>联合采样中满足段内单调的比例。</summary>
    public double MonotonicProbability { get; init; }
    public int Polarity { get; init; }
}

public sealed record UncertaintyBandPoint
{
    public required double DepthM { get; init; }
    public required string SegmentId { get; init; }
    public double? Q025 { get; init; }
    public double? Median { get; init; }
    public double? Q975 { get; init; }
    public bool Extrapolated { get; init; }
}

public sealed record BandValue(double? Q025, double? Median, double? Q975, string SegmentId, bool Extrapolated, string? FailureReason);

public sealed record DerivationOutcome
{
    public required DerivationStatus Status { get; init; }
    public required List<CandidateModel> Candidates { get; init; }
    public required List<Diagnostic> Diagnostics { get; init; }
    public required string InputDigest { get; init; }
    public required string RuleSetId { get; init; }
    /// <summary>预算：并列组合总数（无论是否枚举完）。</summary>
    public int TotalTieCombinations { get; init; }
    public int EnumeratedCombinations { get; init; }
}
