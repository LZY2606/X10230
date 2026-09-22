using ChronoCore.Domain.Events;
using ChronoCore.Domain.Quantities;

namespace ChronoCore.Domain.Derivation;

/// <summary>
/// 深度-年龄模型推导（纯领域服务，测试可直接调用）。
/// 关键规则：
///  1) 年龄只能在不断层段内单调；缺口/不整合是显式边界，绝不跨边界插值；
///  2) 锚点携带完整分布，绝不用均值替代；
///  3) 并列锚点产生稳定排列的全部等优候选；
///  4) 预算耗尽 / 语义无解 / 输入不足是三种不同结果。
/// </summary>
public sealed class AgeModelEngine
{
    private readonly Func<DateTimeOffset> _clock;

    public AgeModelEngine(Func<DateTimeOffset>? clock = null) => _clock = clock ?? (() => DateTimeOffset.UtcNow);

    public DerivationOutcome Derive(ProjectState state, string? ruleSetId = null, int? monteCarloN = null)
    {
        var rsId = ruleSetId ?? state.CurrentRuleSetId;
        if (!state.RuleSets.TryGetValue(rsId, out var rs))
            return Outcome(DerivationStatus.InsufficientInput, rsId,
                new[] { Diag("D-NO-RULES", DiagnosticSeverity.Error, $"规则版本不存在：{rsId}", "ruleset") },
                new List<CandidateModel>(), DigestOf(state, rsId), 0, 0);

        var diags = new List<Diagnostic>();
        var usable = state.Anchors.Values
            .Where(a => !a.Excluded)
            .Where(a => a.Depth.Converted)
            .Where(a => (a.CorrectedDistribution ?? a.Distribution) is not null)
            .ToList();

        foreach (var a in state.Anchors.Values)
        {
            if (!a.Depth.Converted)
                diags.Add(Diag("D-DEPTH", DiagnosticSeverity.Warning,
                    $"锚点 {a.Id} 深度无法转换，排除出推导但保留原值：{a.Depth.RawText}",
                    $"anchors[{a.Id}].depth", a.Id));
            if (a.Diagnostic is not null)
                diags.Add(Diag("D-AGE", DiagnosticSeverity.Warning, a.Diagnostic, $"anchors[{a.Id}].age", a.Id));
            if (a.Excluded)
                diags.Add(Diag("D-EXCLUDED", DiagnosticSeverity.Info,
                    $"锚点 {a.Id} 已按决定排除：{a.ExclusionReason}", $"anchors[{a.Id}]", a.Id));
        }

        var bySegment = usable
            .GroupBy(a => a.SegmentId ?? "")
            .Where(g => !string.IsNullOrEmpty(g.Key) && state.Segments.ContainsKey(g.Key))
            .ToDictionary(g => g.Key, g => g.ToList());

        if (bySegment.Values.Sum(g => g.Count) < 2)
            return Outcome(DerivationStatus.InsufficientInput, rsId, diags.Append(
                Diag("D-INSUFF", DiagnosticSeverity.Error,
                    "可用于推导的锚点不足 2 个，不产生模型（不以默认值伪装成功）", "anchors")).ToArray(),
                new List<CandidateModel>(), DigestOf(state, rsId), 0, 0);

        int n = monteCarloN ?? rs.MonteCarloN;

        // 每段：检测重复深度与并列组，枚举稳定顺序候选。
        var segmentPlans = new List<SegmentPlan>();
        int totalCombinations = 1;
        bool overflow = false;
        foreach (var (segId, anchors) in bySegment)
        {
            var seg = state.Segments[segId];
            var plan = BuildSegmentPlan(seg, anchors, state, rs, diags);
            segmentPlans.Add(plan);
            totalCombinations = SafeMul(totalCombinations, plan.Orderings.Count, ref overflow);
        }

        if (overflow || totalCombinations > rs.MaxTieCombinations)
        {
            diags.Add(Diag("D-BUDGET", DiagnosticSeverity.Error,
                $"并列组合数 {totalCombinations} 超过预算 {rs.MaxTieCombinations}：结果=资源预算耗尽，已枚举 {rs.MaxTieCombinations} 组后停止",
                "anchors.tiGroups"));
            // 预算耗尽仍返回已经枚举的候选（截断），但状态明确不同。
        }

        var candidates = new List<CandidateModel>();
        var enumerated = EnumerateCombinations(segmentPlans, rs.MaxTieCombinations);
        var inputDigest = DigestOf(state, rsId);

        foreach (var combo in enumerated)
        {
            candidates.Add(BuildCandidate(combo, state, rs, n, inputDigest, _clock()));
        }

        // 等优标记：在有效候选中按分数容差分组。
        var valid = candidates.Where(c => c.Valid).OrderBy(c => c.Score).ToList();
        if (valid.Count > 1)
        {
            var best = valid[0].Score;
            double tol = Math.Max(1e-6, Math.Abs(best) * 1e-6);
            foreach (var c in valid)
                c.Equifinal = c.Score <= best + tol;
        }

        var status = candidates.Any(c => c.Valid)
            ? (overflow || totalCombinations > rs.MaxTieCombinations ? DerivationStatus.BudgetExhausted : DerivationStatus.Ok)
            : DerivationStatus.SemanticallyUnsolvable;
        if (status == DerivationStatus.SemanticallyUnsolvable)
            diags.Add(Diag("D-UNSOLV", DiagnosticSeverity.Error,
                "所有候选均不合法：不断层段内年龄无法满足单调，无合法模型（语义无解）", "segments"));

        return Outcome(status, rsId, diags, candidates, inputDigest, totalCombinations, enumerated.Count);
    }

    private static int SafeMul(int a, int b, ref bool overflow)
    {
        try { checked { return a * b; } }
        catch (OverflowException) { overflow = true; return int.MaxValue; }
    }

    private static DerivationOutcome Outcome(DerivationStatus st, string rsId, IEnumerable<Diagnostic> diags,
        List<CandidateModel> cands, string digest, int total, int enumerated) => new()
    {
        Status = st,
        Candidates = cands,
        Diagnostics = diags.ToList(),
        InputDigest = digest,
        RuleSetId = rsId,
        TotalTieCombinations = total,
        EnumeratedCombinations = enumerated
    };

    internal static Diagnostic Diag(string code, DiagnosticSeverity sev, string msg, string loc, string? target = null, string? scn = null)
        => new() { Code = code, Severity = sev, Message = msg, Location = loc, TargetId = target, ScenarioId = scn };

    private static string DigestOf(ProjectState state, string rsId)
    {
        var anchorDigest = state.Anchors.Values.OrderBy(a => a.Id, StringComparer.Ordinal).Select(a => new SortedDictionary<string, object?>
        {
            ["id"] = a.Id,
            ["depthM"] = a.Depth.ValueM,
            ["excluded"] = a.Excluded,
            ["tie"] = a.TieGroupId,
            ["dist"] = (a.CorrectedDistribution ?? a.Distribution)!.Components
                .Select(c => (object?)new { c.MeanYearsBp, c.SdYearsBp, c.Weight }).ToList()
        }).ToList();
        var segDigest = state.Segments.Values.OrderBy(s => s.Id, StringComparer.Ordinal)
            .Select(s => (object?)new { s.Id, polarity = s.Polarity, top = s.Top.ValueM, bottom = s.Bottom.ValueM }).ToList();
        var bndDigest = state.Boundaries.Values.OrderBy(b => b.Id, StringComparer.Ordinal)
            .Select(b => (object?)new { b.Id, kind = b.Kind.ToString(), missing = b.MissingYears }).ToList();
        return Hashing.Sha256Hex(Hashing.CanonicalJson(
            new { rsId, anchors = anchorDigest, segments = segDigest, boundaries = bndDigest }));
    }
}
