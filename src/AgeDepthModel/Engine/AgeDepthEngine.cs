using System.Security.Cryptography;
using System.Text;
using AgeDepthModel.Domain;

namespace AgeDepthModel.Engine;

public sealed record EngineOptions(
    int MaxAnchorsTotal = 512,
    int MaxCandidates = 16,
    /// <summary>并列锚点判定：同深度且中位年龄差超过该值（ka）才构成冲突分支。</summary>
    double TiedAnchorToleranceKa = 0.05);

public sealed record AnchorInput(
    string Id, string SegmentId, double Depth,
    AgeDistribution DistributionKa, string Source);

public sealed record SegmentInput(
    string Id, int Seq, double Top, double Base, SegmentKind Kind, string Label);

public sealed record RateInterval(
    string SegmentId, string FromAnchorId, string ToAnchorId,
    double DepthTop, double DepthBase,
    double RateQ05, double RateQ50, double RateQ95, string Unit);

public sealed record BandPoint(double Depth, double AgeQ05, double AgeQ50, double AgeQ95);

public sealed record SegmentComputation(
    string SegmentId, string InputHash,
    IReadOnlyList<RateInterval> Rates,
    IReadOnlyList<BandPoint> Band,
    IReadOnlyList<Diagnostic> Diagnostics);

public sealed record CandidateComputation(
    string CandidateId, string? ParentId,
    IReadOnlyList<string> AdoptedAnchorIds,
    IReadOnlyList<string> ExcludedAnchorIds,
    IReadOnlyDictionary<string, SegmentComputation> Segments);

public sealed record ModelComputation(
    ResultStatus Status,
    IReadOnlyList<CandidateComputation> Candidates,
    IReadOnlyList<Diagnostic> Diagnostics)
{
    public static ModelComputation Fail(ResultStatus status, params Diagnostic[] diagnostics) =>
        new(status, Array.Empty<CandidateComputation>(), diagnostics);
}

/// <summary>
/// 深度-年龄模型引擎。规则：
/// 1. 年龄只能在不断（Continuous）层段内单调；Gap/Unconformity 是显式边界，不跨边界施加单调约束，也不跨边界计算速率。
/// 2. 锚点分布以分位数参与计算，绝不退化为均值。
/// 3. 倒置层段与并列锚点产生多个等优候选，全部保留并记录谱系。
/// 4. 等价输入得到稳定顺序（先按深度、再按锚点 id 排序）。
/// </summary>
public static class AgeDepthEngine
{
    public static ModelComputation Compute(
        IReadOnlyList<SegmentInput> segments,
        IReadOnlyList<AnchorInput> adoptedAnchors,
        EngineOptions? options = null)
    {
        options ??= new EngineOptions();
        var diagnostics = new List<Diagnostic>();

        if (adoptedAnchors.Count > options.MaxAnchorsTotal)
            return ModelComputation.Fail(ResultStatus.ResourceExhausted,
                new Diagnostic("Budget.Anchors",
                    $"锚点数量 {adoptedAnchors.Count} 超过预算 {options.MaxAnchorsTotal}。", "engine"));

        var continuous = segments
            .Where(s => s.Kind == SegmentKind.Continuous)
            .OrderBy(s => s.Seq)
            .ToList();

        // 稳定顺序：深度相同按 id 排序，等价输入必得等价输出。
        var bySegment = adoptedAnchors
            .GroupBy(a => a.SegmentId)
            .ToDictionary(
                g => g.Key,
                g => (IReadOnlyList<AnchorInput>)g
                    .OrderBy(a => a.Depth)
                    .ThenBy(a => a.Id, StringComparer.Ordinal)
                    .ToList());

        foreach (var segment in continuous)
        {
            if (!bySegment.TryGetValue(segment.Id, out var anchors)) continue;
            // 深度重复：同深度多条记录，全部保留并给出诊断。
            foreach (var dup in anchors.GroupBy(a => a.Depth).Where(g => g.Count() > 1))
                diagnostics.Add(new Diagnostic("Depth.Duplicate",
                    $"深度 {dup.Key} 处存在 {dup.Count()} 条锚点记录（{string.Join(", ", dup.Select(a => a.Id))}），全部保留。",
                    $"segment/{segment.Id}"));
        }

        // 冲突检测：倒置层段 + 并列锚点。
        var conflicts = DetectConflicts(continuous, bySegment, options, diagnostics);

        // 候选生成：基线 + 每个冲突的每个排除变体，全部保留。
        var candidateSets = new List<(string? Parent, List<string> Excluded)>
        {
            (null, new List<string>())
        };
        foreach (var conflict in conflicts)
        {
            foreach (var anchorId in conflict)
            {
                var excluded = new List<string> { anchorId };
                if (!candidateSets.Any(c => c.Excluded.SequenceEqual(excluded)))
                    candidateSets.Add(("M0", excluded));
            }
            if (candidateSets.Count > options.MaxCandidates)
                return ModelComputation.Fail(ResultStatus.ResourceExhausted,
                    new Diagnostic("Budget.Candidates",
                        $"冲突分支数超过候选预算 {options.MaxCandidates}，已保留部分候选。", "engine"));
        }

        var candidates = new List<CandidateComputation>();
        var anySegmentUnsolvable = false;
        for (var i = 0; i < candidateSets.Count; i++)
        {
            var (parent, excluded) = candidateSets[i];
            var candidateId = i == 0 ? "M0" : $"M{i}";
            var adopted = adoptedAnchors.Where(a => !excluded.Contains(a.Id)).ToList();
            var segmentResults = new Dictionary<string, SegmentComputation>();
            foreach (var segment in continuous)
            {
                var anchors = adopted
                    .Where(a => a.SegmentId == segment.Id)
                    .OrderBy(a => a.Depth).ThenBy(a => a.Id, StringComparer.Ordinal)
                    .ToList();
                segmentResults[segment.Id] = ComputeSegment(segment, anchors, ref anySegmentUnsolvable);
            }
            candidates.Add(new CandidateComputation(
                candidateId, parent,
                adopted.Select(a => a.Id).ToList(),
                excluded,
                segmentResults));
        }

        if (candidates.Count > 0 && candidates.All(c =>
                c.Segments.Values.Any(s => s.Diagnostics.Any(d => d.Code == "Segment.Unsolvable"))))
            return new ModelComputation(ResultStatus.SemanticUnsolvable, candidates, diagnostics);

        return new ModelComputation(ResultStatus.Ok, candidates, diagnostics);
    }

    private static List<List<string>> DetectConflicts(
        List<SegmentInput> continuous,
        Dictionary<string, IReadOnlyList<AnchorInput>> bySegment,
        EngineOptions options,
        List<Diagnostic> diagnostics)
    {
        var conflicts = new List<List<string>>();
        foreach (var segment in continuous)
        {
            if (!bySegment.TryGetValue(segment.Id, out var anchors) || anchors.Count < 2)
                continue;
            // 并列锚点：同深度、分布中位差超过容差。
            foreach (var group in anchors.GroupBy(a => a.Depth).Where(g => g.Count() > 1))
            {
                var members = group.ToList();
                var medians = members.Select(m => m.DistributionKa.Median).ToList();
                if (medians.Max() - medians.Min() > options.TiedAnchorToleranceKa)
                {
                    diagnostics.Add(new Diagnostic("Anchor.Tied",
                        $"深度 {group.Key} 处并列锚点中位年龄差 {medians.Max() - medians.Min():F3} ka，产生等优候选分支。",
                        $"segment/{segment.Id}"));
                    conflicts.Add(members.Select(m => m.Id).ToList());
                }
            }
            // 倒置层段：随深度增加，年龄分位区间显著倒流。
            for (var i = 0; i < anchors.Count - 1; i++)
            {
                var upper = anchors[i];
                var lower = anchors[i + 1];
                if (upper.Depth == lower.Depth) continue; // 并列锚点已处理
                if (upper.DistributionKa.Quantile(0.05) > lower.DistributionKa.Quantile(0.95))
                {
                    diagnostics.Add(new Diagnostic("Section.Inverted",
                        $"锚点 {upper.Id}（{upper.Depth}）q05 年龄大于 {lower.Id}（{lower.Depth}）q95 年龄，层段倒置。",
                        $"segment/{segment.Id}"));
                    conflicts.Add(new List<string> { upper.Id, lower.Id });
                }
            }
        }
        return conflicts;
    }

    private static SegmentComputation ComputeSegment(
        SegmentInput segment, List<AnchorInput> anchors, ref bool anyUnsolvable)
    {
        var diagnostics = new List<Diagnostic>();
        var rates = new List<RateInterval>();
        var band = new List<BandPoint>();

        var inputHash = ComputeInputHash(segment, anchors);

        for (var i = 0; i < anchors.Count - 1; i++)
        {
            var upper = anchors[i];
            var lower = anchors[i + 1];
            var dz = lower.Depth - upper.Depth;
            if (dz <= 0) continue; // 并列锚点之间无速率区间
            var da50 = lower.DistributionKa.Median - upper.DistributionKa.Median;
            if (da50 <= 0)
            {
                diagnostics.Add(new Diagnostic("Segment.Unsolvable",
                    $"候选内 {upper.Id} → {lower.Id} 中位年龄随深度倒流（{da50:F3} ka），该区间无法计算速率。",
                    $"segment/{segment.Id}"));
                anyUnsolvable = true;
                continue;
            }
            // 速率区间（m/ka）：用分位数传播，不用均值。
            var daFast = Math.Max(1e-6,
                lower.DistributionKa.Quantile(0.05) - upper.DistributionKa.Quantile(0.95));
            var daSlow = Math.Max(1e-6,
                lower.DistributionKa.Quantile(0.95) - upper.DistributionKa.Quantile(0.05));
            rates.Add(new RateInterval(
                segment.Id, upper.Id, lower.Id,
                upper.Depth, lower.Depth,
                RateQ05: dz / daSlow, RateQ50: dz / da50, RateQ95: dz / daFast,
                Unit: "m/ka"));
        }

        foreach (var anchor in anchors)
        {
            band.Add(new BandPoint(
                anchor.Depth,
                anchor.DistributionKa.Quantile(0.05),
                anchor.DistributionKa.Median,
                anchor.DistributionKa.Quantile(0.95)));
        }

        return new SegmentComputation(segment.Id, inputHash, rates, band, diagnostics);
    }

    /// <summary>输入摘要哈希：支撑局部重算——只有输入变化的层段哈希才会变。</summary>
    public static string ComputeInputHash(SegmentInput segment, IReadOnlyList<AnchorInput> anchors)
    {
        var sb = new StringBuilder();
        sb.Append(segment.Id).Append('|').Append(segment.Top).Append('|').Append(segment.Base);
        foreach (var a in anchors.OrderBy(x => x.Depth).ThenBy(x => x.Id, StringComparer.Ordinal))
        {
            sb.Append('|').Append(a.Id).Append(':').Append(a.Depth)
              .Append(':').Append(a.DistributionKa.Quantile(0.05))
              .Append(':').Append(a.DistributionKa.Median)
              .Append(':').Append(a.DistributionKa.Quantile(0.95));
        }
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(sb.ToString())))[..16];
    }
}
