using System.Security.Cryptography;
using System.Text;
using AgeDepthStudio.Domain;

namespace AgeDepthStudio.Engine;

/// <summary>
/// 年龄-深度模型推导。核心规则：
/// 1) 年龄只能在不断层段内单调；缺口/不整合是显式边界，插值永不跨越。
/// 2) 锚点分布不能被均值替代：曲线带 q01..q99 分位，长尾保留并标记。
/// 3) 多个等优候选全部保留，按稳定规则排序。
/// 4) 资源预算耗尽 / 格式损坏 / 语义无解是三种不同结果。
/// </summary>
public sealed class ModelEngine
{
    public sealed record EngineOutput(
        ModelResult Result,
        IReadOnlyList<(string Type, string SegmentId, string PayloadJson)> Judgments);

    public EngineOutput Compute(ProjectedState s, RuleParams p, string modelId, string triggerEventId)
    {
        var judgments = new List<(string, string, string)>();
        var segResults = new List<SegmentResult>();
        var overall = OutcomeKind.Ok;

        var segments = s.Segments
            .Select(seg => (seg, top: Depth.TryParseMeters(seg.TopDepthRaw, out var t) ? t : double.NaN,
                                   bas: Depth.TryParseMeters(seg.BaseDepthRaw, out var b) ? b : double.NaN))
            .OrderBy(x => x.top).ThenBy(x => x.seg.SegmentId, StringComparer.Ordinal)
            .ToList();

        foreach (var (seg, top, bas) in segments)
        {
            var segDiag = new List<Diagnostic>();
            if (double.IsNaN(top) || double.IsNaN(bas))
            {
                segDiag.Add(new Diagnostic("SEGMENT_DEPTH_UNCONVERTIBLE", "error",
                    $"分段 {seg.SegmentId} 的深度无法转换，原值保留：top='{seg.TopDepthRaw}', base='{seg.BaseDepthRaw}'",
                    seg.SegmentId, "/topDepthRaw"));
                segResults.Add(new SegmentResult(seg.SegmentId, OutcomeKind.FormatCorrupt, Array.Empty<CandidateModel>(), segDiag));
                overall = Worst(overall, OutcomeKind.FormatCorrupt);
                continue;
            }

            var anchors = new List<(AgeAnchor a, AnchorDerived d)>();
            foreach (var a in s.Anchors.Where(a => a.SegmentId == seg.SegmentId)
                     .OrderBy(a => a.AnchorId, StringComparer.Ordinal))
            {
                if (!Depth.TryParseMeters(a.DepthRaw, out var dm))
                {
                    segDiag.Add(new Diagnostic("ANCHOR_DEPTH_UNCONVERTIBLE", "error",
                        $"锚点 {a.AnchorId} 深度 '{a.DepthRaw}' 无法转换，值留在原位并跳过计算", a.AnchorId, "/depthRaw"));
                    continue;
                }
                if (dm < top - 1e-9 || dm > bas + 1e-9)
                {
                    segDiag.Add(new Diagnostic("ANCHOR_OUT_OF_SEGMENT", "error",
                        $"锚点 {a.AnchorId} 深度 {dm}m 超出分段 [{top},{bas}]m", a.AnchorId, "/depthRaw"));
                    continue;
                }
                if (a.Samples.Length == 0)
                {
                    segDiag.Add(new Diagnostic("ANCHOR_NO_SAMPLES", "error",
                        $"锚点 {a.AnchorId} 缺少分布样本，拒绝以空值伪装成功", a.AnchorId, "/samples"));
                    continue;
                }
                var off = s.Offsets.FirstOrDefault(o => o.AnchorId == a.AnchorId);
                var z = Stats.Correct(a.Samples, off, p);
                var q01 = Stats.Quantile(z, .01); var q05 = Stats.Quantile(z, .05);
                var q50 = Stats.Quantile(z, .50); var q95 = Stats.Quantile(z, .95);
                var q99 = Stats.Quantile(z, .99);
                var longTail = (q99 - q50) > p.LongTailRatio * Math.Max(q50 - q01, 1e-9);
                if (longTail)
                    judgments.Add(("judgment.long_tail", seg.SegmentId, JsonSerializer.Serialize(new
                    {
                        rule = "long-tail-retained",
                        anchorId = a.AnchorId,
                        basis = "一个锚点的分布不能被均值替代：长尾分布整体保留，仅以 q01..q99 分位进入曲线",
                        q01, q50, q99, ratio = p.LongTailRatio
                    })));
                anchors.Add((a, new AnchorDerived(a.AnchorId, dm, Stats.Mean(z), Stats.Std(z), q01, q05, q50, q95, q99, longTail)));
            }

            var excluded = new HashSet<string>(StringComparer.Ordinal);
            foreach (var g in s.Decisions
                         .Where(d => d.Action is DecisionAction.ExcludeAnchor or DecisionAction.ReinstateAnchor)
                         .GroupBy(d => d.TargetId))
            {
                var last = g.OrderBy(d => d.DecisionId, StringComparer.Ordinal).Last();
                if (last.Action == DecisionAction.ExcludeAnchor) excluded.Add(g.Key);
                foreach (var (a, _) in anchors.Where(x => excluded.Contains(x.a.AnchorId) && x.a.AnchorId == g.Key))
                    segDiag.Add(new Diagnostic("ANCHOR_EXCLUDED_BY_DECISION", "info",
                        $"锚点 {a.AnchorId} 被决定排除（决定保留，可复核）", a.AnchorId, null));
            }
            var usable = anchors.Where(x => !excluded.Contains(x.a.AnchorId))
                .OrderBy(x => x.d.DepthM).ThenBy(x => x.a.AnchorId, StringComparer.Ordinal).ToList();

            var groups = usable.GroupBy(x => x.d.DepthM).OrderBy(g => g.Key).ToList();
            foreach (var g in groups.Where(g => g.Count() > 1))
                judgments.Add(("judgment.tie_anchors", seg.SegmentId, JsonSerializer.Serialize(new
                {
                    rule = "tied-anchors-enumerated",
                    depthM = g.Key,
                    anchorIds = g.Select(x => x.a.AnchorId).ToArray(),
                    basis = "并列锚点不被合并：每个组合形成一个等优候选，全部保留"
                })));

            long combos = 1;
            foreach (var g in groups) combos = checked(combos * g.Count());
            if (combos > p.EnumerationBudget)
            {
                segDiag.Add(new Diagnostic("BUDGET_EXHAUSTED", "error",
                    $"候选组合数 {combos} 超过预算 {p.EnumerationBudget}；结果类别为 BudgetExhausted，不同于格式损坏或语义无解",
                    seg.SegmentId, null));
                judgments.Add(("judgment.budget_exhausted", seg.SegmentId, JsonSerializer.Serialize(new
                { rule = "enumeration-budget", combos, budget = p.EnumerationBudget })));
                segResults.Add(new SegmentResult(seg.SegmentId, OutcomeKind.BudgetExhausted, Array.Empty<CandidateModel>(), segDiag));
                overall = Worst(overall, OutcomeKind.BudgetExhausted);
                continue;
            }

            var candidates = new List<CandidateModel>();
            var chosen = new (AgeAnchor a, AnchorDerived d)[groups.Count];
            var spent = 0;
            void Enumerate(int gi)
            {
                if (spent >= p.EnumerationBudget) return;
                if (gi == groups.Count)
                {
                    spent++;
                    var cand = BuildCandidate(seg.SegmentId, chosen.ToList(), judgments);
                    if (cand != null) candidates.Add(cand);
                    return;
                }
                foreach (var item in groups[gi].OrderBy(x => x.a.AnchorId, StringComparer.Ordinal))
                {
                    chosen[gi] = item;
                    Enumerate(gi + 1);
                }
            }
            if (groups.Count > 0) Enumerate(0);

            if (candidates.Count == 0 && usable.Count > 0)
            {
                segDiag.Add(new Diagnostic("SEMANTIC_UNSOLVABLE_INVERSION", "error",
                    "倒置层段：深部锚点确定年轻于浅部锚点（q99(浅) < q01(深)），在不跨越显式边界前提下无解",
                    seg.SegmentId, null));
                judgments.Add(("judgment.inversion_unsolvable", seg.SegmentId, JsonSerializer.Serialize(new
                {
                    rule = "strict-inversion-unsolvable",
                    basis = "年龄只能在不断层段中单调；深度缺口和不整合是显式边界，不允许跨边界借调"
                })));
                segResults.Add(new SegmentResult(seg.SegmentId, OutcomeKind.SemanticUnsolvable,
                    Array.Empty<CandidateModel>(), segDiag));
                overall = Worst(overall, OutcomeKind.SemanticUnsolvable);
                continue;
            }

            var minScore = candidates.Count > 0 ? candidates.Min(c => c.Score) : 0;
            var kept = candidates.Where(c => c.Score <= minScore + 1e-9)
                .OrderBy(c => string.Join(",", c.AnchorIds), StringComparer.Ordinal).ToList();
            if (kept.Count > 1)
                judgments.Add(("judgment.equal_optimum_kept", seg.SegmentId, JsonSerializer.Serialize(new
                {
                    rule = "equal-optimum-retained",
                    candidateIds = kept.Select(c => c.CandidateId).ToArray(),
                    score = minScore,
                    basis = "多个等优模型需保留：不做任意单选"
                })));
            var result = new SegmentResult(seg.SegmentId, OutcomeKind.Ok, kept, segDiag);
            segResults.Add(result);
            judgments.Add(("judgment.segment_status", seg.SegmentId, JsonSerializer.Serialize(new
            {
                segmentId = seg.SegmentId,
                outcome = result.Outcome.ToString(),
                candidateCount = kept.Count,
                anchorIds = usable.Select(x => x.a.AnchorId).ToArray(),
                diagnostics = segDiag.Select(d => d.Code).ToArray()
            })));
        }

        foreach (var g in s.Gaps.OrderBy(g => g.GapId, StringComparer.Ordinal))
            judgments.Add(("judgment.explicit_boundary", g.AboveSegmentId, JsonSerializer.Serialize(new
            {
                rule = "explicit-boundary",
                gapId = g.GapId,
                kind = g.Kind.ToString(),
                above = g.AboveSegmentId,
                below = g.BelowSegmentId,
                basis = "深度缺口和不整合是显式边界：插值与单调约束均不跨越"
            })));

        var fingerprint = Fingerprint(s, p);
        var model = new ModelResult(modelId, p.ParamsId, fingerprint, segResults, overall);
        return new EngineOutput(model, judgments);
    }

    private CandidateModel? BuildCandidate(string segmentId, List<(AgeAnchor a, AnchorDerived d)> chosen,
        List<(string, string, string)> judgments)
    {
        for (var i = 0; i + 1 < chosen.Count; i++)
            if (chosen[i].d.Q99 < chosen[i + 1].d.Q01 - 1e-12) return null;

        var n = chosen.Count;
        var mu = chosen.Select(c => c.d.MuKa).ToArray();
        var w = chosen.Select(c => 1.0 / Math.Max(c.d.SigmaKa * c.d.SigmaKa, 1e-9)).ToArray();
        var blocks = Enumerable.Range(0, n).Select(i => new List<int> { i }).ToList();
        for (var i = 0; i + 1 < blocks.Count;)
        {
            var m1 = PoolMean(blocks[i], mu, w);
            var m2 = PoolMean(blocks[i + 1], mu, w);
            if (m1 > m2 + 1e-12)
            {
                blocks[i].AddRange(blocks[i + 1]);
                blocks.RemoveAt(i + 1);
                if (i > 0) i--;
            }
            else i++;
        }
        var fitted = new double[n];
        foreach (var b in blocks)
        {
            var pm = PoolMean(b, mu, w);
            foreach (var idx in b) fitted[idx] = pm;
        }
        var score = chosen.Select((c, i) =>
        {
            var s2 = Math.Max(c.d.SigmaKa, 1e-6);
            var r = (c.d.MuKa - fitted[i]) / s2;
            return r * r;
        }).Sum();

        var knots = new CurveKnot[n];
        for (var i = 0; i < n; i++)
        {
            var d = chosen[i].d;
            // 池化块成员共享拟合中位；分位带取块内成员的包络，不确定性不外推到无锚点处。
            var block = blocks.First(b => b.Contains(i));
            var members = block.Select(idx => chosen[idx].d).ToList();
            knots[i] = new CurveKnot(d.DepthM,
                members.Min(m => m.Q01), members.Min(m => m.Q05), fitted[i],
                members.Max(m => m.Q95), members.Max(m => m.Q99));
        }

        var rates = new List<RateInterval>();
        for (var i = 0; i + 1 < knots.Length; i++)
        {
            var dz = knots[i + 1].DepthM - knots[i].DepthM;
            if (dz <= 1e-9) continue;
            // 速率区间由相邻锚点分位带包络导出，绝不跨越缺口/不整合。
            var dtMin = Math.Max(knots[i + 1].Q01 - knots[i].Q99, 1e-9);
            var dtMax = Math.Max(knots[i + 1].Q99 - knots[i].Q01, 1e-9);
            rates.Add(new RateInterval(knots[i].DepthM, knots[i + 1].DepthM,
                1000.0 * dz / dtMax, 1000.0 * dz / dtMin, "mm/ka"));
        }
        var anchorIds = chosen.Select(c => c.a.AnchorId).ToArray();
        var candId = $"cand_{segmentId}_{string.Join("-", anchorIds)}";
        return new CandidateModel(candId, segmentId, anchorIds, Math.Round(score, 6), knots, rates.ToArray());
    }

    private static double PoolMean(List<int> idx, double[] mu, double[] w)
    {
        double sw = 0, sm = 0;
        foreach (var i in idx) { sw += w[i]; sm += w[i] * mu[i]; }
        return sm / Math.Max(sw, 1e-12);
    }

    private static OutcomeKind Worst(OutcomeKind a, OutcomeKind b) =>
        (a, b) switch
        {
            (OutcomeKind.Ok, var x) => x,
            (var x, OutcomeKind.Ok) => x,
            (OutcomeKind.FormatCorrupt, _) or (_, OutcomeKind.FormatCorrupt) => OutcomeKind.FormatCorrupt,
            (OutcomeKind.SemanticUnsolvable, _) or (_, OutcomeKind.SemanticUnsolvable) => OutcomeKind.SemanticUnsolvable,
            _ => OutcomeKind.BudgetExhausted
        };

    /// <summary>段级输入指纹：局部重算时验证未受影响段保持一致。</summary>
    public static string SegmentFingerprint(ProjectedState s, RuleParams p, string segmentId)
    {
        var canon = JsonSerializer.Serialize(new
        {
            segment = s.Segments.Where(x => x.SegmentId == segmentId),
            gaps = s.Gaps.Where(x => x.AboveSegmentId == segmentId || x.BelowSegmentId == segmentId),
            anchors = s.Anchors.Where(x => x.SegmentId == segmentId).OrderBy(x => x.AnchorId, StringComparer.Ordinal),
            offsets = s.Offsets.Where(o => s.Anchors.Any(a => a.AnchorId == o.AnchorId && a.SegmentId == segmentId)),
            decisions = s.Decisions.Where(d => s.Anchors.Any(a => a.SegmentId == segmentId && a.AnchorId == d.TargetId)),
            p.EnumerationBudget, p.AgeInflation, p.LongTailRatio
        });
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(canon)))[..16].ToLowerInvariant();
    }

    public static string Fingerprint(ProjectedState s, RuleParams p)
    {
        var canon = JsonSerializer.Serialize(new
        {
            segments = s.Segments.OrderBy(x => x.SegmentId, StringComparer.Ordinal),
            gaps = s.Gaps.OrderBy(x => x.GapId, StringComparer.Ordinal),
            anchors = s.Anchors.OrderBy(x => x.AnchorId, StringComparer.Ordinal),
            offsets = s.Offsets.OrderBy(x => x.OffsetId, StringComparer.Ordinal),
            layers = s.Layers.OrderBy(x => x.LayerId, StringComparer.Ordinal),
            decisions = s.Decisions.OrderBy(x => x.DecisionId, StringComparer.Ordinal),
            p.EnumerationBudget, p.AgeInflation, p.LongTailRatio
        });
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(canon)))[..16].ToLowerInvariant();
    }
}
