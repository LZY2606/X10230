using System.Text.Json;
using AgeModel.Domain;

namespace AgeModel.Engine;

/// <summary>
/// 深度-年龄模型推导。核心规则只在这里（服务端/存储层可直接测试，不在浏览器）：
/// 1) 以不整合/岩芯断裂为显式边界切连续层段；跨边界不插值、不要求单调。
/// 2) 锚点分布经储层偏移卷积校正，绝不以均值替代。
/// 3) 层段内枚举全部“以足够概率单调”的合法候选；等优候选全部保留；超预算=独立结果 BudgetExhausted。
/// 4) 每个候选做确定性联合采样，输出不确定度带与速率区间。
/// </summary>
public sealed class DerivationEngine
{
    public DerivationOutput Derive(SourceMaterials src, RuleVersion rules,
        IReadOnlyList<FrozenModel>? freezes = null, IReadOnlyList<Decision>? decisions = null)
    {
        var diags = new List<Diagnostic>();
        var events = new List<JudgmentEvent>();

        var basisIssue = ValidateBasis(src);
        if (basisIssue is not null)
        {
            diags.Add(basisIssue);
            return new DerivationOutput(Id("deriv", src.SourceId + rules.Id), src.SourceId, rules.Id,
                [], [], [], diags, OutcomeKind.CorruptFormat, false);
        }

        var sections = BuildSections(src.Core);
        var sectionResults = new List<SectionResult>();
        bool budgetHit = false, anyNoSolution = false, anyFatal = false;

        var reservoirById = src.Reservoirs.ToDictionary(r => r.Id);

        foreach (var section in sections)
        {
            var sDiags = new List<Diagnostic>();
            var sEvents = new List<JudgmentEvent>();
            string sp = $"$.sections#{section.Id}";

            var inSection = src.Anchors
                .Where(a => a.At.Cm >= section.Top.Cm && a.At.Cm <= section.Bottom.Cm)
                .ToList();

            // 排除决定：保留但不参与建模；产生机器可读判定事件。
            var usable = new List<PreparedAnchor>();
            foreach (var a in inSection)
            {
                if (a.Exclusion is not null)
                {
                    sEvents.Add(JEvent($"excl:{a.Id}", "anchor-excluded", sp + $".anchors#{a.Id}",
                        "rejected", rules, src,
                        new { a.Exclusion.Reason, a.Exclusion.DecidedBy, a.Exclusion.DecidedAt }, [a.Id]));
                    continue;
                }
                if (!a.Distribution.IsUsable())
                {
                    sDiags.Add(new Diagnostic("UNUSABLE-ANCHOR", Severity.Error,
                        sp + $".anchors#{a.Id}", $"锚点 {a.Id} 的年龄无法转换，不能用于建模（保留原位置）。"));
                    sEvents.Add(JEvent($"unconv:{a.Id}", "anchor-unconverted", sp + $".anchors#{a.Id}",
                        "diagnostic", rules, src, new { ((UnconvertedDist)a.Distribution).Reason }, [a.Id]));
                    continue;
                }
                ReservoirOffset? res = a.ReservoirId is not null && reservoirById.TryGetValue(a.ReservoirId, out var rr) ? rr : null;
                if (a.ReservoirId is not null && res is null)
                    sDiags.Add(new Diagnostic("MISSING-RESERVOIR", Severity.Warning,
                        sp + $".anchors#{a.Id}", $"锚点引用的储层偏移 {a.ReservoirId} 不存在，按未校正处理。"));

                var corrected = Sampling.CorrectReservoir(a.Distribution, res, rules.MonteCarloDraws,
                    $"{src.SourceId}:{rules.Id}:{a.Id}");
                var us = Sampling.StratifiedQuantiles(rules.MonteCarloDraws, $"{src.SourceId}:{rules.Id}:{a.Id}");
                var draws = corrected.Draw(us);
                usable.Add(new PreparedAnchor(a, corrected, draws, draws[(int)(0.5 * draws.Length)]));
                if (res is not null)
                    sEvents.Add(JEvent($"res:{a.Id}", "reservoir-correction", sp + $".anchors#{a.Id}",
                        "accepted", rules, src, new { reservoirId = res.Id, res.Source }, [a.Id, res.Id]));
            }

            // 深度重复：同深度多锚点是“并列锚点”，合法且必须保留；同深度同 id 才是损坏。
            var dupGroups = usable.GroupBy(u => u.Anchor.At.Cm).Where(g => g.Count() > 1).ToList();
            var seenIds = new HashSet<string>();
            bool corrupt = false;
            foreach (var a in inSection)
            {
                if (!seenIds.Add(a.Id))
                {
                    sDiags.Add(new Diagnostic("DUP-ANCHOR-ID", Severity.Error, sp, $"锚点 id {a.Id} 重复（格式损坏）。"));
                    corrupt = true;
                }
            }
            foreach (var g in dupGroups)
                sEvents.Add(JEvent($"tie:{section.Id}:{g.Key}", "tied-anchors", sp + $"@depth={g.Key}",
                    "inconclusive", rules, src,
                    new { depthCm = g.Key, anchorIds = g.Select(x => x.Anchor.Id).ToArray() },
                    g.Select(x => x.Anchor.Id).ToArray()));

            if (corrupt)
            {
                sectionResults.Add(new SectionResult(section.Id, [], OutcomeKind.CorruptFormat, sDiags, false, null));
                anyFatal = true;
                events.AddRange(sEvents);
                continue;
            }

            // 倒置层段检测（中位数顺序），生成判定事件，但不武断丢弃：由候选枚举处理。
            var ordered = usable.OrderBy(u => u.Anchor.At.Cm).ToList();
            for (int i = 1; i < ordered.Count; i++)
            {
                if (ordered[i].MedianAge > ordered[i - 1].MedianAge + 1e-9)
                    sEvents.Add(JEvent($"inv:{ordered[i].Anchor.Id}", "inversion-detected",
                        sp + $".anchors#{ordered[i].Anchor.Id}", "diagnostic", rules, src,
                        new { deeper = ordered[i].Anchor.Id, shallower = ordered[i - 1].Anchor.Id,
                              deeperMedian = ordered[i].MedianAge, shallowerMedian = ordered[i - 1].MedianAge },
                        [ordered[i].Anchor.Id, ordered[i - 1].Anchor.Id]));
            }

            // 单调 DAG：边 = 较深锚点年龄足够大概率更老。
            var candidates = EnumerateCandidates(ordered, rules, src, section, sEvents, sDiags,
                out bool budget, out bool noSolution);
            budgetHit |= budget; anyNoSolution |= noSolution;

            var freeze = freezes?.FirstOrDefault(f => f.SectionId == section.Id);
            var outcome = noSolution ? OutcomeKind.SemanticNoSolution
                : budget ? OutcomeKind.BudgetExhausted : OutcomeKind.Ok;

            sectionResults.Add(new SectionResult(section.Id, candidates, outcome, sDiags,
                freeze is not null, freeze?.CandidateId));
            events.AddRange(sEvents);
        }

        events.Add(JEvent("derive", "derivation-complete", "$",
            anyNoSolution ? "inconclusive" : "accepted", rules, src,
            new { sections = sections.Count, budgetHit, anyNoSolution },
            sections.Select(s => s.Id).ToArray()));

        var overall = anyFatal ? OutcomeKind.CorruptFormat
            : anyNoSolution ? OutcomeKind.SemanticNoSolution
            : budgetHit ? OutcomeKind.BudgetExhausted : OutcomeKind.Ok;

        return new DerivationOutput(
            Id("deriv", src.SourceId + ":" + rules.Id + ":" + string.Join(",", decisions?.Select(d => d.Id) ?? [])),
            src.SourceId, rules.Id, sections, sectionResults, events, diags, overall, budgetHit);
    }

    private List<ModelCandidate> EnumerateCandidates(List<PreparedAnchor> ordered, RuleVersion rules,
        SourceMaterials src, ContiguousSection section, List<JudgmentEvent> events, List<Diagnostic> diags,
        out bool budgetHit, out bool noSolution)
    {
        budgetHit = false; noSolution = false;
        int n = ordered.Count;
        string sp = $"$.sections#{section.Id}";

        if (n < 2)
        {
            diags.Add(new Diagnostic("UNDERCONSTRAINED", Severity.Warning, sp,
                n == 0 ? "层段内无可用锚点，无法建立深度-年龄模型。" : "层段内仅一个锚点，无法约束速率/单调性。"));
            if (n == 0) noSolution = true;
        }

        // edge i->j（i 深、j 浅，i 在列表后）成立的概率 >= 阈值。
        bool EdgeOk(PreparedAnchor deeper, PreparedAnchor shallower)
        {
            double p = Sampling.MonotonicityProbability(deeper.Draws, shallower.Draws);
            return p >= rules.MonotonicityProbabilityThreshold;
        }

        // 以“保留最多证据 + 最大单调概率”为目标，DFS 枚举合法链（深度递增=年龄递减）。
        var validPaths = new List<List<int>>();
        var edgeProb = new double[n, n];
        for (int i = 0; i < n; i++)
            for (int j = i + 1; j < n; j++)
                edgeProb[i, j] = Sampling.MonotonicityProbability(ordered[j].Draws, ordered[i].Draws);

        bool truncated = false;
        void Dfs(int last, List<int> path)
        {
            if (validPaths.Count >= rules.CandidateBudget) { truncated = true; return; }
            bool extended = false;
            for (int ni = last + 1; ni < n; ni++)
            {
                bool ok = true;
                foreach (var pi in path)
                    if (edgeProb[pi, ni] < rules.MonotonicityProbabilityThreshold) { ok = false; break; }
                if (!ok) continue;
                extended = true;
                path.Add(ni);
                Dfs(ni, path);
                path.RemoveAt(path.Count - 1);
                if (truncated) return;
            }
            if (path.Count >= 2) validPaths.Add(new List<int>(path));
            else if (!extended && path.Count == 1) { /* 单锚点不足，忽略 */ }
        }
        for (int s = 0; s < n && !truncated; s++) Dfs(s, new List<int> { s });

        if (truncated)
        {
            budgetHit = true;
            diags.Add(new Diagnostic("BUDGET-EXHAUSTED", Severity.Warning, sp,
                $"合法候选数量超过预算 {rules.CandidateBudget}；保留前 {validPaths.Count} 个并显式标记截断。"));
            events.Add(JEvent($"budget:{section.Id}", "budget-exhausted", sp, "inconclusive", rules, src,
                new { budget = rules.CandidateBudget, retained = validPaths.Count }, section.Id.Yield()));
        }

        if (validPaths.Count == 0 && n >= 2)
        {
            noSolution = true;
            diags.Add(new Diagnostic("NO-MONOTONE-SOLUTION", Severity.Error, sp,
                "层段内不存在满足单调概率阈值的锚点链（倒置层段语义无解，不以默认值伪装）。"));
            events.Add(JEvent($"nosol:{section.Id}", "no-monotone-solution", sp, "rejected", rules, src,
                new { threshold = rules.MonotonicityProbabilityThreshold }, [section.Id]));
            return [];
        }

        // 评分：先比链长（证据覆盖），再比单调概率乘积，再比总精度。
        double Score(List<int> path)
        {
            double logp = 0, precision = 0;
            for (int k = 0; k < path.Count - 1; k++)
            {
                double p = Math.Max(edgeProb[path[k], path[k + 1]], 1e-6);
                logp += Math.Log(p);
            }
            foreach (var idx in path)
            {
                var (q05, _, q95) = ordered[idx].Distribution.Summary();
                if (double.IsFinite(q95 - q05) && q95 - q05 > 0) precision -= Math.Log(q95 - q05);
            }
            return path.Count * 1_000_000 + logp * 1000 + precision;
        }

        var scored = validPaths
            .Select(p => (path: p, score: Score(p)))
            .OrderByDescending(x => x.score)
            .ThenBy(x => string.Join(",", x.path.Select(i => ordered[i].Anchor.Id)), StringComparer.Ordinal)
            .ToList();

        if (scored.Count == 0) return [];

        double best = scored[0].score;
        var candidates = new List<ModelCandidate>();
        var usedAnchorIds = new HashSet<string>();
        for (int rank = 0; rank < scored.Count; rank++)
        {
            var (path, score) = scored[rank];
            bool isBest = Math.Abs(score - best) < 1e-9;
            var anchorIds = path.Select(i => ordered[i].Anchor.Id).ToList();
            foreach (var id in anchorIds) usedAnchorIds.Add(id);

            string canonical = string.Join("|", anchorIds) + "|" + rules.Id + "|" + src.SourceId;
            string digest = Sampling.StableDigest(canonical);
            string candidateId = "cand-" + digest[..12];

            var (bands, rates) = BuildBandsAndRates(path.Select(i => ordered[i]).ToList(), rules, candidateId);

            candidates.Add(new ModelCandidate
            {
                CandidateId = candidateId,
                Rank = rank + 1,
                IsEquivalentBest = isBest,
                Score = Math.Round(score, 6),
                AnchorIdsInOrder = anchorIds,
                TiedAnchorIds = ordered.Where(u => !usedAnchorIds.Contains(u.Anchor.Id))
                    .Select(u => u.Anchor.Id).Distinct().ToList(),
                Bands = bands,
                Rates = rates,
                RuleVersionId = rules.Id,
                SourceId = src.SourceId,
                InputDigest = digest,
                ParentDependencyDigests = path.Select(i =>
                    Sampling.StableDigest($"anchor:{src.SourceId}:{ordered[i].Anchor.Id}")).Distinct().ToList()
            });
        }

        var bests = candidates.Where(c => c.IsEquivalentBest).ToList();
        if (bests.Count > 1)
            events.Add(JEvent($"tiebest:{section.Id}", "equivalent-best-models", sp, "inconclusive", rules, src,
                new { candidateIds = bests.Select(b => b.CandidateId).ToArray() },
                bests.SelectMany(b => b.AnchorIdsInOrder).Distinct().ToArray()));

        return candidates;
    }

    /// <summary>确定性联合采样：各层用同一分层样本线性插值，保证深度间年龄相关，得到连续不确定度带。</summary>
    private (IReadOnlyList<UncertaintyBand>, IReadOnlyList<RateBand>) BuildBandsAndRates(
        List<PreparedAnchor> chain, RuleVersion rules, string candidateId)
    {
        // chain 按深度升序（浅→深）。
        var us = Sampling.StratifiedQuantiles(rules.MonteCarloDraws, candidateId + ":band");
        int D = rules.MonteCarloDraws;
        var anchorDraws = chain.Select(a => a.Distribution.Draw(us)).ToArray();

        double z0 = chain[0].Anchor.At.Cm, z1 = chain[^1].Anchor.At.Cm;
        int G = Math.Max(2, rules.RateGridPoints);

        var bands = new List<UncertaintyBand>();
        var rates = new List<RateBand>();
        for (int gi = 0; gi <= G; gi++)
        {
            double z = z0 + (z1 - z0) * gi / G;
            var ages = new double[D];
            // 找包围锚点段。
            int k = 0;
            while (k < chain.Count - 1 && z > chain[k + 1].Anchor.At.Cm) k++;
            double za = chain[k].Anchor.At.Cm;
            double zb = chain[Math.Min(k + 1, chain.Count - 1)].Anchor.At.Cm;
            double t = zb > za ? (z - za) / (zb - za) : 0;
            for (int s = 0; s < D; s++)
                ages[s] = anchorDraws[k][s] + t * (anchorDraws[Math.Min(k + 1, chain.Count - 1)][s] - anchorDraws[k][s]);
            var sorted = (double[])ages.Clone(); Array.Sort(sorted);
            bands.Add(new UncertaintyBand(z, Quant(sorted, .05), Quant(sorted, .50), Quant(sorted, .95)));
        }

        // 速率 = |d age / d depth|，单位换算到 cm/ka（cal BP / cm * 1000）。逐 draw 计算再分位。
        for (int gi = 0; gi < G; gi++)
        {
            var b0 = bands[gi]; var b1 = bands[gi + 1];
            double dzCm = b1.DepthCm - b0.DepthCm;
            double zMid = (b0.DepthCm + b1.DepthCm) / 2;
            var r = new double[D];
            for (int s = 0; s < D; s++)
            {
                // 重算该 draw 在两端的年龄（与 bands 一致地线性）。
                double a0 = DrawAt(chain, anchorDraws, b0.DepthCm, s);
                double a1 = DrawAt(chain, anchorDraws, b1.DepthCm, s);
                double agePerCm = Math.Abs(a1 - a0) / dzCm;        // cal yr/cm
                r[s] = agePerCm * 1000.0;                          // → cm/ka 倒数语义：沉积速率 cm/ka = 1/(yr/cm)*1000
                r[s] = 1.0 / ((agePerCm == 0) ? 1e-9 : agePerCm) * 1000.0; // cm/ka
            }
            Array.Sort(r);
            rates.Add(new RateBand(zMid, Quant(r, .05), Quant(r, .50), Quant(r, .95), "cm/ka"));
        }
        return (bands, rates);
    }

    private static double DrawAt(PreparedAnchor[] chain, double[][] draws, double z, int s)
    {
        int k = 0;
        while (k < chain.Length - 1 && z > chain[k + 1].Anchor.At.Cm) k++;
        double za = chain[k].Anchor.At.Cm;
        double zb = chain[Math.Min(k + 1, chain.Length - 1)].Anchor.At.Cm;
        double t = zb > za ? (z - za) / (zb - za) : 0;
        return draws[k][s] + t * (draws[Math.Min(k + 1, chain.Length - 1)][s] - draws[k][s]);
    }

    private static double Quant(double[] sorted, double p)
    {
        double idx = p * (sorted.Length - 1);
        int lo = (int)Math.Floor(idx); int hi = Math.Min(lo + 1, sorted.Length - 1);
        return sorted[lo] + (sorted[hi] - sorted[lo]) * (idx - lo);
    }

    /// <summary>按 Barrier 缺口（不整合/岩芯断裂）切分连续层段；普通采样缺口保留在层段内。</summary>
    public static IReadOnlyList<ContiguousSection> BuildSections(CoreProfile core)
    {
        var result = new List<ContiguousSection>();
        var barriers = core.Gaps.Where(g => g.IsBarrier)
            .OrderBy(g => g.Top.Cm).ToList();
        double cursor = core.Top.Cm;
        int idx = 1;
        foreach (var b in barriers)
        {
            var inside = core.Gaps.Where(g => !g.IsBarrier &&
                g.Top.Cm >= cursor && g.Bottom.Cm <= b.Top.Cm).Select(g => g.Id).ToList();
            result.Add(new ContiguousSection($"sec-{idx}",
                new Depth(cursor, core.Top.Datum), new Depth(b.Top.Cm, core.Top.Datum), inside));
            cursor = b.Bottom.Cm; idx++;
        }
        var tailInside = core.Gaps.Where(g => !g.IsBarrier &&
            g.Top.Cm >= cursor && g.Bottom.Cm <= core.Bottom.Cm).Select(g => g.Id).ToList();
        result.Add(new ContiguousSection($"sec-{idx}",
            new Depth(cursor, core.Top.Datum), new Depth(core.Bottom.Cm, core.Top.Datum), tailInside));
        return result.Where(s => s.Bottom.Cm >= s.Top.Cm).ToList();
    }

    private static Diagnostic? ValidateBasis(SourceMaterials src)
    {
        if (double.IsNaN(src.Core.Top.Cm) || double.IsNaN(src.Core.Bottom.Cm))
            return new Diagnostic("BAD-CORE-RANGE", Severity.Error, "$.core", "岩芯顶/底深度无法规范化。");
        if (src.Core.Bottom.Cm < src.Core.Top.Cm)
            return new Diagnostic("INVERTED-CORE", Severity.Error, "$.core", "岩芯底深度浅于顶深度（深度轴倒置/格式损坏）。");
        foreach (var g in src.Core.Gaps)
            if (g.Bottom.Cm < g.Top.Cm)
                return new Diagnostic("BAD-GAP", Severity.Error, $"$.core.gaps#{g.Id}", "缺口底浅于顶（格式损坏）。");
        return null;
    }

    private static JudgmentEvent JEvent(string key, string type, string path, string status,
        RuleVersion rules, SourceMaterials src, object detail, IReadOnlyList<string> affected) => new()
    {
        EventId = "evt-" + Sampling.StableDigest($"{src.SourceId}|{rules.Id}|{key}|{path}")[..16],
        Type = type,
        At = path,
        Status = status,
        RuleVersionId = rules.Id,
        SourceId = src.SourceId,
        DetailJson = JsonSerializer.Serialize(detail, JsonOpt),
        AffectedObjectIds = affected
    };

    private static string Id(string prefix, string content) => prefix + "-" + Sampling.StableDigest(content)[..12];

    private static readonly JsonSerializerOptions JsonOpt = new(JsonSerializerDefaults.Web);

    private sealed record PreparedAnchor(AgeAnchor Anchor, AgeDistribution Distribution, double[] Draws, double MedianAge);
}

internal static class EnumerableExtensions
{
    public static IReadOnlyList<string> Yield(this string s) => [s];
}
