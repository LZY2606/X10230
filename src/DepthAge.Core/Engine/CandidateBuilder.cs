using DepthAge.Core.Models;

namespace DepthAge.Core.Engine;

/// <summary>
/// 候选模型枚举：
/// - 同深度并列锚点按深度分组，逐组选择一个代表，所有合法组合都保留；
/// - 年龄倒置时枚举最小舍弃集（每个倒置对至少排除一个），并提供 PAVA 合并解；
/// - 等优（分数差 &lt;= TieTolerance）候选全部标记并列，不唯一化；
/// - 等价输入产生稳定顺序（RankKey 字典序）；
/// - 枚举超过 CandidateBudget 立即停止并返回 BudgetExhausted。
/// </summary>
public sealed class CandidateBuilder
{
    private int _emitted;
    private bool _budgetHit;

    public List<CandidateModel> Build(
        List<ModelSegment> segments,
        Dictionary<string, PreparedAnchor> prepared,
        RuleBook rules,
        string computationId,
        List<Judgment> judgments,
        Action<Judgment> judge,
        DateTimeOffset now)
    {
        _emitted = 0;
        _budgetHit = false;
        var candidates = new List<CandidateModel>();

        foreach (var segment in segments)
        {
            if (!segment.Solvable)
            {
                judge(Judgment("J_SEG_INVERTED", "Blocked",
                    "倒置层段不存在合法单调年龄模型：语义无解，等待人工决定（材料与诊断保留）",
                    segment.AnchorIds, segment, rules, now));
                continue;
            }
            if (segment.AnchorIds.Count == 0) continue;

            var groups = GroupByDepth(segment, prepared);
            EmitDuplicateDepthJudgments(groups, segment, rules, judge, now);

            var combos = EnumerateRepresentatives(groups, rules, judge, segment, now);
            foreach (var combo in combos)
            {
                var representativeIds = combo;
                // 方法 1：最小舍弃集线性插值
                foreach (var retained in EnumerateMinimalDrops(representativeIds, prepared, segment, rules, judge, now))
                {
                    TryEmit(candidates, "Linear", retained, segment, prepared, rules, computationId, judgments, judge, now);
                    if (_budgetHit) break;
                }
                if (_budgetHit) break;
                // 方法 2：PAVA（保序回归合并）——不删除锚点的另一种合法解
                TryEmit(candidates, "PAVA", representativeIds, segment, prepared, rules, computationId, judgments, judge, now, poolViolators: true);
            }
            if (_budgetHit) break;
        }

        Rank(candidates, rules);
        return candidates;
    }

    public bool BudgetHit => _budgetHit;

    private void TryEmit(List<CandidateModel> candidates, string method, List<string> anchorIds,
        ModelSegment segment, Dictionary<string, PreparedAnchor> prepared, RuleBook rules,
        string computationId, List<Judgment> judgments, Action<Judgment> judge, DateTimeOffset now,
        bool poolViolators = false)
    {
        if (_emitted >= rules.CandidateBudget)
        {
            _budgetHit = true;
            judge(Judgment("J_BUDGET_EXHAUSTED", "Blocked",
                $"候选枚举达到预算上限 {rules.CandidateBudget}：返回已枚举出的候选并标记 BudgetExhausted，不静默截断为唯一解",
                new[] { segment.Id }, segment, rules, now));
            return;
        }

        var ordered = anchorIds
            .Select(id => prepared[id])
            .OrderBy(p => p.DepthM).ThenBy(p => p.Anchor.Id, StringComparer.Ordinal)
            .ToList();

        var (rates, pooledNote) = poolViolators
            ? RateFactory.PavaRates(segment.Id, ordered)
            : RateFactory.LinearRates(segment.Id, ordered);

        var validRates = rates.Where(r => r.InvalidationReason is null).ToList();
        var violations = rates.Where(r => !r.AgeDirectionOk && r.InvalidationReason is null).ToList();
        if (!poolViolators && violations.Count > 0)
        {
            // 线性解出现倒置却未被舍弃集消除：判定拒绝
            judge(Judgment("J_MONOTONY_VIOLATION", "Rejected",
                "线性候选在不断层段内违反‘下覆更老’的单调约束，拒绝该候选",
                violations.SelectMany(r => new[] { r.FromAnchorId, r.ToAnchorId }).Distinct().ToList(),
                segment, rules, now));
            return;
        }

        var bands = BandFactory.ForSegment(segment, ordered, poolViolators);
        var usedIds = ordered.Select(p => p.Anchor.Id).ToList();
        var droppedIds = segment.AnchorIds.Except(usedIds).ToList();

        foreach (var dropped in droppedIds)
            judge(Judgment("J_ANCHOR_DROPPED", "Retained",
                $"候选中锚点 {dropped} 因段内单调倒置被舍弃；锚点材料本身保留，可由其它候选继续使用",
                new[] { dropped }, segment, rules, now));
        if (pooledNote is not null)
            judge(Judgment("J_PAVA_POOLED", "Accepted",
                pooledNote, usedIds, segment, rules, now));

        var score = Score(validRates);
        var attr = new Dictionary<string, string>
        {
            ["segmentId"] = segment.Id,
            ["effectiveTopM"] = segment.TopDepthM.ToString("R"),
            ["effectiveBottomM"] = segment.BottomDepthM.ToString("R"),
            ["anchorCount"] = usedIds.Count.ToString()
        };
        var candidate = new CandidateModel
        {
            Id = StableId.Compute("cand", new { c = computationId, m = method, s = segment.Id, u = string.Join(",", usedIds) }),
            RankKey = $"{segment.Id}|{method}|{score:R}|{string.Join(",", usedIds)}",
            Method = method,
            Rank = -1,
            TiedWithBest = false,
            Score = score,
            UsedAnchorIds = usedIds,
            DroppedAnchorIds = droppedIds,
            Rates = rates,
            Bands = bands,
            SegmentIds = new List<string> { segment.Id },
            Predictions = ordered.SelectMany(p => p.Quantiles.Where(q => q.P == 0.5).Select(q => new QuantilePoint(p.DepthM, q.CalYrBP))).ToList(),
            Attributes = attr
        };
        candidates.Add(candidate);
        _emitted++;
    }

    private static double Score(List<RateInterval> rates)
    {
        if (rates.Count == 0) return double.PositiveInfinity;
        return rates.Sum(r =>
        {
            var width = Math.Log1p(Math.Max(0, r.HighCmPerKyr - r.LowCmPerKyr));
            var inversionPenalty = r.AgeDirectionOk ? 0 : 1000;
            return width + inversionPenalty;
        });
    }

    private static void Rank(List<CandidateModel> candidates, RuleBook rules)
    {
        var bySegment = candidates.GroupBy(c => c.Attributes["segmentId"]);
        foreach (var group in bySegment)
        {
            var ordered = group.OrderBy(c => c.Score).ThenBy(c => c.RankKey, StringComparer.Ordinal).ToList();
            var best = ordered[0].Score;
            var rank = 0;
            foreach (var candidate in ordered)
            {
                rank++;
                candidates[candidates.IndexOf(candidate)] = candidate with
                {
                    Rank = rank,
                    TiedWithBest = Math.Abs(candidate.Score - best) <= rules.TieTolerance
                };
            }
        }
    }

    private static List<List<PreparedAnchor>> GroupByDepth(ModelSegment segment, Dictionary<string, PreparedAnchor> prepared)
    {
        return segment.AnchorIds.Select(id => prepared[id])
            .GroupBy(p => Math.Round(p.DepthM, 9))
            .OrderBy(g => g.Key)
            .Select(g => g.OrderBy(p => p.Anchor.Id, StringComparer.Ordinal).ToList())
            .ToList();
    }

    private static void EmitDuplicateDepthJudgments(List<List<PreparedAnchor>> groups, ModelSegment segment,
        RuleBook rules, Action<Judgment> judge, DateTimeOffset now)
    {
        foreach (var group in groups.Where(g => g.Count > 1))
        {
            var ids = group.Select(p => p.Anchor.Id).ToList();
            var identical = group.Select(p => p.Quantiles.FirstOrDefault(q => q.P == 0.5)?.CalYrBP ?? 0)
                                 .Distinct().Count() == 1;
            judge(Judgment(identical ? "J_TIED_ANCHORS" : "J_DUPLICATE_DEPTH",
                identical ? "Retained" : "Warning",
                identical
                    ? $"深度 {group.Key:R} m 处并列锚点分布等价：每个选择都是等优候选，全部保留"
                    : $"深度 {group.Key:R} m 处存在深度重复但年龄分布不同的锚点：按并列锚点逐一枚举，不用均值合并",
                ids, segment, rules, now,
                new Dictionary<string, string> { ["depthM"] = group.Key.ToString("R"), ["identical"] = identical.ToString() }));
        }
    }

    private List<List<string>> EnumerateRepresentatives(List<List<PreparedAnchor>> groups, RuleBook rules,
        Action<Judgment> judge, ModelSegment segment, DateTimeOffset now)
    {
        var combos = new List<List<string>> { new() };
        foreach (var group in groups)
        {
            var next = new List<List<string>>();
            foreach (var combo in combos)
            foreach (var pick in group)
            {
                next.Add(new List<string>(combo) { pick.Anchor.Id });
                if (next.Count >= rules.CandidateBudget)
                {
                    _budgetHit = true;
                    judge(Judgment("J_BUDGET_EXHAUSTED", "Blocked",
                        $"并列锚点组合枚举达到预算 {rules.CandidateBudget}：标记 BudgetExhausted",
                        group.Select(p => p.Anchor.Id).ToList(), segment, rules, now));
                    return next;
                }
            }
            combos = next;
        }
        return combos;
    }

    private IEnumerable<List<string>> EnumerateMinimalDrops(List<string> representativeIds,
        Dictionary<string, PreparedAnchor> prepared, ModelSegment segment, RuleBook rules,
        Action<Judgment> judge, DateTimeOffset now)
    {
        var ordered = representativeIds
            .Select(id => prepared[id])
            .OrderBy(p => p.DepthM).ThenBy(p => p.Anchor.Id, StringComparer.Ordinal)
            .ToList();
        var violatingPairs = new List<(int I, int J)>();
        for (var i = 0; i < ordered.Count - 1; i++)
        for (var j = i + 1; j < ordered.Count; j++)
        {
            var older = Median(ordered[j]);
            var younger = Median(ordered[i]);
            if (older < younger) // 更深反而更年轻 => 倒置
                violatingPairs.Add((i, j));
        }

        if (violatingPairs.Count == 0)
        {
            yield return ordered.Select(p => p.Anchor.Id).ToList();
            yield break;
        }

        // 枚举最小顶点覆盖（小集合可行）；预算耗尽即停
        var full = ordered.Select((p, idx) => idx).ToHashSet();
        var emittedSets = new HashSet<string>();
        foreach (var cover in MinimumVertexCovers(violatingPairs, rules, ref _budgetHit))
        {
            if (_budgetHit) yield break;
            var retained = ordered.Where((_, idx) => !cover.Contains(idx)).Select(p => p.Anchor.Id).ToList();
            var key = string.Join(",", retained);
            if (emittedSets.Add(key))
                yield return retained;
        }
    }

    internal static IEnumerable<HashSet<int>> MinimumVertexCovers(List<(int I, int J)> pairs, RuleBook rules, ref bool budgetHit)
    {
        var emitted = 0;
        var maxSize = pairs.Max(p => Math.Max(p.I, p.J)) + 1;
        for (var mask = 0; mask < (1 << maxSize); mask++)
        {
            var set = new HashSet<int>();
            for (var k = 0; k < maxSize; k++) if ((mask & (1 << k)) != 0) set.Add(k);
            if (pairs.All(p => set.Contains(p.I) || set.Contains(p.J)))
            {
                yield return set;
                emitted++;
                if (emitted >= rules.CandidateBudget) { budgetHit = true; yield break; }
            }
        }
    }

    private static double Median(PreparedAnchor p) =>
        p.Quantiles.First(q => q.P == 0.5).CalYrBP;

    private static Judgment Judgment(string code, string status, string statement, List<string> affected,
        ModelSegment segment, RuleBook rules, DateTimeOffset now, Dictionary<string, string>? attr = null)
    {
        return new Judgment
        {
            Id = StableId.Compute("judg", new { code, seg = segment.Id, aff = string.Join(",", affected), statement }),
            Code = code,
            Status = status,
            Statement = statement,
            AffectedIds = affected,
            SegmentId = segment.Id,
            RuleVersion = rules.Version,
            Attributes = attr ?? new(),
            RecordedAt = now
        };
    }
}
