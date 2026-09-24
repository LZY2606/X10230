namespace AgeDepth.Engine;

/// <summary>规范化后的锚点（派生层）。原始表示仍在材料层。</summary>
public sealed record Anchor(string MaterialId, double DepthM, double[] SamplesCalBp)
{
    public double Median => Quantile(SamplesCalBp, 0.5);
    public static double Quantile(IReadOnlyList<double> sorted, double q)
    {
        if (sorted.Count == 0) return double.NaN;
        var pos = q * (sorted.Count - 1);
        var lo = (int)Math.Floor(pos);
        var hi = Math.Min(lo + 1, sorted.Count - 1);
        return sorted[lo] + (sorted[hi] - sorted[lo]) * (pos - lo);
    }
}

public sealed record Boundary(string MaterialId, double DepthM, string Kind, string Reason);

public sealed record Segment(string Id, double TopM, double BottomM,
    IReadOnlyList<Anchor> Anchors, IReadOnlyList<Boundary> Boundaries);

public sealed record RateInterval(double FromM, double ToM, double Lower, double Median, double Upper)
{
    public const string Unit = "m/kyr";
}

/// <summary>一套可辩护候选模型。多套等优候选全部保留。</summary>
public sealed record CandidateModel(
    string Id, string SegmentId, int Generation,
    IReadOnlyList<string> UsedAnchorIds,
    IReadOnlyList<string> ExcludedAnchorIds,
    string? ParentCandidateId,
    string RuleVersion,
    string LineageNote,
    double[] DepthGrid,
    double[] Lower, double[] Median, double[] Upper,   // calBP 分位带
    IReadOnlyList<RateInterval> Rates);

/// <summary>结构化状态：不是日志，是可重放证据。</summary>
public sealed record StructuredState(string Code, string Position, string Detail,
    IReadOnlyList<string> AffectedObjectIds);

public sealed record SegmentModel(Segment Segment, IReadOnlyList<CandidateModel> Candidates,
    IReadOnlyList<StructuredState> States);

public sealed record ModelSet(
    IReadOnlyList<SegmentModel> Segments,
    IReadOnlyList<StructuredState> States,
    string RuleVersion);
