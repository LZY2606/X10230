using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace AgeDepth.Core;

public sealed record ModelParams(
    int Draws = 400, int GridPoints = 60, double TieToleranceM = 1e-6,
    int MaxCandidates = 8, int DrawBudget = 200_000)
{
    public string Version { get; init; } = "params/1";
}

public sealed record ControlPoint(double DepthM, double AgeKa);
public sealed record RateInterval(double TopM, double BottomM, double P05, double P50, double P95, string Unit);
public sealed record UncertaintyBand(
    double[] Depths, double[] P05, double[] P50, double[] P95,
    string AgeUnit, string DepthUnit, string Datum);

public sealed record ModelCandidate(
    string Id, string SegmentId, ControlPoint[] Controls, RateInterval[] Rates,
    UncertaintyBand Band, double Score, string Status,
    string[] UsedAnchorIds, string[] ExcludedAnchorIds,
    string InputHash, string ParamVersion, string[] Parents);

public sealed record SegmentBuildResult(
    List<ModelCandidate> Candidates, List<Diagnostic> Diagnostics,
    bool BudgetExhausted = false, string? BudgetDetail = null);

public static class ModelBuilder
{
    /// <summary>
    /// 在单个不断层段内构建候选年龄-深度模型。
    /// 规则：年龄只能随深度单调不减；并列锚点与冲突锚点产生多个等优候选，全部保留；
    /// 锚点分布全程参与蒙特卡洛，不用均值替代。
    /// </summary>
    public static SegmentBuildResult BuildSegment(
        Segment segment, IReadOnlyList<AgeAnchor> anchors, ModelParams p,
        string inputHash, string[] parents, Random rng)
    {
        var diags = new List<Diagnostic>();
        if (segment.Inverted)
        {
            diags.Add(new Diagnostic("SEGMENT_SKIPPED_INVERTED", $"segment:{segment.Id}",
                "倒置层段不参与单调建模，锚点保留在档案中", "warning"));
            return new SegmentBuildResult(new(), diags);
        }

        // 可用锚点：accepted、深度可规范化、落在段内、分布有效
        var usable = new List<(AgeAnchor a, double depth, AgeDist age)>();
        foreach (var a in anchors.Where(a => a.Status == "accepted"))
        {
            if (a.Depth.Canonical is not { } d)
            {
                diags.Add(new Diagnostic("ANCHOR_DEPTH_UNCONVERTIBLE", $"anchor:{a.Id}",
                    $"锚点 {a.Id} 深度无法规范化，保留原始表示 '{a.Depth.Raw}'，不参与计算", "warning"));
                continue;
            }
            if (d < segment.TopM - 1e-9 || d > segment.BottomM + 1e-9) continue;
            var age = a.Age;
            if (a.ReservoirOffset?.Canonical is { } off)
                age = age.Shift(off); // 储层偏移：平移分布而非替换
            if (!age.IsValid)
            {
                diags.Add(new Diagnostic("ANCHOR_DIST_INVALID", $"anchor:{a.Id}",
                    $"锚点 {a.Id} 年龄分布无效（kind={a.Age.Kind}），不参与计算", "warning"));
                continue;
            }
            usable.Add((a, d, age));
        }
        if (usable.Count == 0)
            return new SegmentBuildResult(new(), diags);

        // 预算检查：资源耗尽是独立结果，不伪装成功
        var totalDraws = (long)p.Draws * Math.Max(1, p.MaxCandidates) * usable.Count;
        if (p.Draws <= 0 || p.GridPoints <= 0 || totalDraws > p.DrawBudget)
            return new SegmentBuildResult(new(), diags, true,
                $"预算耗尽: 需要 {totalDraws} 次抽样 > 预算 {p.DrawBudget}");

        usable.Sort((x, y) => x.depth != y.depth
            ? x.depth.CompareTo(y.depth)
            : string.CompareOrdinal(x.a.Id, y.a.Id)); // 等价输入 → 稳定顺序

        // 并列锚点检测
        var tieGroups = usable.GroupBy(x => Math.Round(x.depth / p.TieToleranceM))
            .Where(g => g.Count() > 1).ToList();
        foreach (var g in tieGroups)
            diags.Add(new Diagnostic("ANCHOR_TIED", $"anchor:{string.Join(",", g.Select(x => x.a.Id))}",
                $"并列锚点于 {g.First().depth:0.###} m: {string.Join(", ", g.Select(x => x.a.Id))}；为每个选择生成等优候选", "warning"));

        // 候选生成：并列组每个成员各代表一个候选；单调冲突时逐个排除冲突锚点
        var candidateSets = EnumerateMonotonicSets(usable, p, diags);
        if (candidateSets.Count == 0)
        {
            diags.Add(new Diagnostic("MODEL_UNSOLVABLE", $"segment:{segment.Id}",
                "不存在满足单调性的锚点子集，语义无解"));
            return new SegmentBuildResult(new(), diags);
        }

        var candidates = new List<ModelCandidate>();
        foreach (var set in candidateSets.Take(p.MaxCandidates))
        {
            var used = set.Select(x => x.a.Id).ToArray();
            var excluded = usable.Select(x => x.a.Id).Except(used).ToArray();
            var (controls, rates, band) = Simulate(segment, set, p, rng);
            var score = -excluded.Length; // 排除越少越优
            var id = CandidateId(segment.Id, used, p.Version);
            candidates.Add(new ModelCandidate(
                id, segment.Id, controls, rates, band, score, "kept",
                used, excluded, inputHash, p.Version, parents));
        }

        var best = candidates.Max(c => c.Score);
        candidates = candidates.Select(c => c with { Status = c.Score == best ? "equal-best" : "kept" }).ToList();
        candidates = candidates
            .OrderByDescending(c => c.Score)
            .ThenBy(c => c.Id, StringComparer.Ordinal)
            .ToList();
        return new SegmentBuildResult(candidates, diags);
    }

    private static List<List<(AgeAnchor a, double depth, AgeDist age)>> EnumerateMonotonicSets(
        List<(AgeAnchor a, double depth, AgeDist age)> usable, ModelParams p, List<Diagnostic> diags)
    {
        // 并列组：每组至多取一个成员 → 笛卡尔组合（有界）
        var groups = usable.GroupBy(x => Math.Round(x.depth / p.TieToleranceM))
            .OrderBy(g => g.First().depth).Select(g => g.ToList()).ToList();
        var combos = new List<List<(AgeAnchor a, double depth, AgeDist age)>> { new() };
        foreach (var g in groups)
        {
            var next = new List<List<(AgeAnchor a, double depth, AgeDist age)>>();
            foreach (var combo in combos)
                foreach (var member in g)
                    next.Add(combo.Concat(new[] { member }).ToList());
            combos = next;
            if (combos.Count > p.MaxCandidates * 4) break; // 有界枚举
        }

        var results = new List<List<(AgeAnchor a, double depth, AgeDist age)>>();
        foreach (var combo in combos)
        {
            if (IsMonotonic(combo)) { results.Add(combo); continue; }
            // 单调冲突：为每个冲突锚点生成“排除其一”的候选
            var conflicts = FindConflicts(combo);
            foreach (var bad in conflicts)
            {
                var reduced = combo.Where(x => x.a.Id != bad.a.Id).ToList();
                if (IsMonotonic(reduced))
                {
                    diags.Add(new Diagnostic("MONOTONIC_VIOLATION", $"anchor:{bad.a.Id}",
                        $"锚点 {bad.a.Id} 中位年龄 {bad.age.Median:0.###} ka 与更浅锚点冲突；生成排除该锚点的候选", "warning"));
                    results.Add(reduced);
                }
            }
        }
        // 去重（按使用锚点集合）
        return results
            .GroupBy(set => string.Join("|", set.Select(x => x.a.Id).OrderBy(x => x, StringComparer.Ordinal)))
            .Select(g => g.First()).ToList();
    }

    private static bool IsMonotonic(List<(AgeAnchor a, double depth, AgeDist age)> set)
    {
        for (var i = 1; i < set.Count; i++)
            if (set[i].age.Median < set[i - 1].age.Median - 1e-9) return false;
        return true;
    }

    private static List<(AgeAnchor a, double depth, AgeDist age)> FindConflicts(
        List<(AgeAnchor a, double depth, AgeDist age)> set)
    {
        var bad = new HashSet<string>();
        for (var i = 1; i < set.Count; i++)
            if (set[i].age.Median < set[i - 1].age.Median - 1e-9)
            {
                bad.Add(set[i].a.Id); bad.Add(set[i - 1].a.Id);
            }
        return set.Where(x => bad.Contains(x.a.Id)).ToList();
    }

    private static (ControlPoint[], RateInterval[], UncertaintyBand) Simulate(
        Segment segment, List<(AgeAnchor a, double depth, AgeDist age)> set, ModelParams p, Random rng)
    {
        var n = set.Count;
        var draws = new double[n][];
        for (var i = 0; i < n; i++) draws[i] = set[i].age.Draw(p.Draws, rng);

        // 网格
        var grid = new double[p.GridPoints];
        for (var g = 0; g < p.GridPoints; g++)
            grid[g] = segment.TopM + (segment.BottomM - segment.TopM) * g / (p.GridPoints - 1);

        var agesAt = new double[p.GridPoints][];
        for (var g = 0; g < p.GridPoints; g++) agesAt[g] = new double[p.Draws];

        var rateSamples = new List<double>[Math.Max(0, n - 1)];
        for (var i = 0; i < n - 1; i++) rateSamples[i] = new List<double>();

        for (var d = 0; d < p.Draws; d++)
        {
            // 单调化：沿深度累计取最大（分布不被均值替代，抽样保持单调约束）
            var ages = new double[n];
            ages[0] = draws[0][d];
            for (var i = 1; i < n; i++) ages[i] = Math.Max(ages[i - 1], draws[i][d]);
            for (var g = 0; g < p.GridPoints; g++)
                agesAt[g][d] = Interp(set.Select(x => x.depth).ToArray(), ages, grid[g]);
            for (var i = 0; i < n - 1; i++)
            {
                var dAge = ages[i + 1] - ages[i];
                if (dAge > 1e-9)
                    rateSamples[i].Add((set[i + 1].depth - set[i].depth) * 100.0 / dAge); // cm/ka
            }
        }

        var band = new UncertaintyBand(
            grid,
            grid.Select((_, g) => QuantileOf(agesAt[g], 0.05)).ToArray(),
            grid.Select((_, g) => QuantileOf(agesAt[g], 0.50)).ToArray(),
            grid.Select((_, g) => QuantileOf(agesAt[g], 0.95)).ToArray(),
            "ka", "m", "mbsf");

        var rates = new RateInterval[n - 1];
        for (var i = 0; i < n - 1; i++)
        {
            var s = rateSamples[i];
            rates[i] = s.Count == 0
                ? new RateInterval(set[i].depth, set[i + 1].depth, 0, 0, 0, "cm/ka")
                : new RateInterval(set[i].depth, set[i + 1].depth,
                    QuantileOf(s.ToArray(), 0.05), QuantileOf(s.ToArray(), 0.5), QuantileOf(s.ToArray(), 0.95), "cm/ka");
        }

        var controls = set.Select(x => new ControlPoint(x.depth, x.age.Median)).ToArray();
        return (controls, rates, band);
    }

    private static double Interp(double[] xs, double[] ys, double x)
    {
        if (x <= xs[0]) return ys[0];
        if (x >= xs[^1]) return ys[^1];
        for (var i = 1; i < xs.Length; i++)
            if (x <= xs[i])
            {
                var t = (x - xs[i - 1]) / (xs[i] - xs[i - 1]);
                return ys[i - 1] + t * (ys[i] - ys[i - 1]);
            }
        return ys[^1];
    }

    private static double QuantileOf(double[] values, double p)
    {
        var sorted = values.OrderBy(x => x).ToArray();
        var pos = p * (sorted.Length - 1);
        var lo = (int)Math.Floor(pos);
        var hi = Math.Min(lo + 1, sorted.Length - 1);
        return sorted[lo] + (sorted[hi] - sorted[lo]) * (pos - lo);
    }

    public static string CandidateId(string segmentId, string[] usedAnchorIds, string paramVersion)
    {
        var key = $"{segmentId}|{string.Join(",", usedAnchorIds.OrderBy(x => x, StringComparer.Ordinal))}|{paramVersion}";
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(key)))[..8].ToLowerInvariant();
        return $"cand-{hash}";
    }

    public static string HashMaterials(object materials, string paramVersion)
    {
        var json = JsonSerializer.Serialize(materials) + "|" + paramVersion;
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(json)))[..16].ToLowerInvariant();
    }
}
