using ChronoCore.Domain;

namespace ChronoCore.Engine;

public sealed partial class AgeModelEngine
{
    private const int BandSamplesPerSegment = 24;

    internal List<CandidateModel> BuildCandidates(
        List<EffectiveAnchor> anchors, List<SegmentCtx> segments,
        ParameterSet p, List<DiagnosticEntry> diag, string? scopeMaterialId)
    {
        // 三套可辩护策略；等优时全部保留（见 RankAndMarkTies）。
        var strategies = new (string Code, string Name, Func<EffectiveAnchor, bool> Keep)[]
        {
            ("all-retained", "全部采纳锚点：并列锚点各自保留（默认叙事）", _ => true),
            ("radiometric-only", "仅放射性碳锚点：事件相关单列为旁证，不进主模型", a => !a.IsEventAnchor),
            ("longtail-flagged", "旧碳标记模型：长尾锚点整体降权展示，仅保留其下置信界约束", a => !a.LongTail),
        };
        _ = strategies;

        var scopeDepth = scopeMaterialId is null ? (double?)null
            : anchors.FirstOrDefault(a => a.SourceMaterialId == scopeMaterialId)?.DepthMbsf
              ?? segments.FirstOrDefault(s => s.SourceMaterialId == scopeMaterialId)?.Top;

        var results = new List<CandidateModel>();
        foreach (var (code, name, keep) in strategies)
        {
            var kept = anchors.Where(keep).Where(a => a.SegmentId != "seg_UNBOUNDED").ToList();
            var model = new CandidateModel
            {
                CandidateId = "cand_" + code,
                UsedAnchorIds = kept.Select(a => a.AnchorId).ToList(),
                ExcludedAnchorIds = anchors.Select(a => a.AnchorId).Except(kept.Select(b => b.AnchorId)).ToList()
            };

            var feasible = true;
            double totalWidth = 0;
            double coveredMeters = 0;
            double spanMeters = 0;
            var perSegmentDraws = new Dictionary<string, double[][]>();

            foreach (var seg in segments.Where(s => !s.Overturned))
            {
                var sa = kept.Where(a => a.SegmentId == seg.SegmentId)
                    .OrderBy(a => a.DepthMbsf).ThenBy(a => a.AnchorId, StringComparer.Ordinal).ToList();
                if (sa.Count < 2)
                {
                    if (sa.Count == 1)
                        diag.Add(new DiagnosticEntry { Code = "I-SINGLE-ANCHOR-SEG", Severity = Severity.Info, Location = seg.SegmentId, Message = $"{name}: 段 {seg.SegmentId} 仅有单锚，无法约束速率区间" });
                    continue;
                }

                var n = p.MonteCarloDraws;
                var matrix = new double[sa.Count][];
                for (var i = 0; i < sa.Count; i++)
                {
                    var a = sa[i];
                    var mu = a.MeanCalYrBp; var sd = (a.Q84 - a.Q16) / 2.0;
                    matrix[i] = DrawNormal(mu, Math.Max(sd, 1), n, StableSeed(model.CandidateId + "|" + a.AnchorId));
                }
                _drawsUsed += n * sa.Count;

                var validCount = 0;
                var rates = new List<List<double>>();
                var bandAtDepth = new Dictionary<double, List<double>>();
                var sampleDepths = SampleDepths(seg, sa);
                foreach (var dd in sampleDepths) bandAtDepth[dd] = new List<double>(n / 8 + 1);

                for (var draw = 0; draw < n; draw++)
                {
                    var ok = true;
                    for (var i = 1; i < sa.Count; i++)
                        if (matrix[i][draw] <= matrix[i - 1][draw]) { ok = false; break; } // 深度增大→年龄增大
                    if (!ok) continue;
                    validCount++;
                    for (var i = 1; i < sa.Count; i++)
                    {
                        var dDepthM = sa[i].DepthMbsf - sa[i - 1].DepthMbsf;
                        if (dDepthM <= 0) continue; // 并列锚点不构成速率
                        var dAgeKa = Math.Abs(matrix[i][draw] - matrix[i - 1][draw]) / 1000.0;
                        if (rates.Count < i) for (var k = rates.Count; k < i; k++) rates.Add(new List<double>());
                        rates[i - 1].Add(dDepthM * 100.0 / Math.Max(dAgeKa, 1e-6));
                    }
                    if (draw % 8 == 0)
                        foreach (var dd in sampleDepths)
                            bandAtDepth[dd].Add(InterpAge(sa, matrix, draw, dd));
                }

                var validFraction = (double)validCount / n;
                if (validFraction < 0.5)
                {
                    feasible = false;
                    model.InfeasibleReason ??= "";
                    model.InfeasibleReason += $"段 {seg.SegmentId} 单调抽样有效率 {validFraction:P0} < 50%；";
                    diag.Add(new DiagnosticEntry { Code = "E-NONMONOTONIC", Severity = Severity.Error, Location = seg.SegmentId, Message = $"{name}: 年龄在不断层段 {seg.SegmentId} 内不单调（有效率 {validFraction:P1}）" });
                }
                else if (validFraction < 0.95)
                    diag.Add(new DiagnosticEntry { Code = "W-WEAK-MONOTONIC", Severity = Severity.Warning, Location = seg.SegmentId, Message = $"{name}: 段 {seg.SegmentId} 单调有效率 {validFraction:P1}，结果带较宽不确定性" });

                for (var i = 1; i < sa.Count; i++)
                {
                    var list = (rates.Count >= i ? rates[i - 1] : new List<double>()).Order().ToArray();
                    if (list.Length == 0) continue;
                    var intervalId = "rate_" + seg.SegmentId + "_" + i.ToString("D2");
                    if (scopeDepth is double sd2 && (sd2 < sa[i - 1].DepthMbsf - 1e-9 || sd2 > sa[i].DepthMbsf + 1e-9)) continue;
                    model.Rates.Add(new RateInterval
                    {
                        IntervalId = intervalId,
                        SegmentId = seg.SegmentId,
                        TopDepth = sa[i - 1].DepthMbsf,
                        BaseDepth = sa[i].DepthMbsf,
                        ValidDrawFraction = validFraction,
                        MedianCmPerKa = Quantile(list, .5),
                        Q025CmPerKa = Quantile(list, .025),
                        Q975CmPerKa = Quantile(list, .975)
                    });
                }

                foreach (var (dd, vals) in bandAtDepth)
                {
                    if (vals.Count < 8) continue;
                    var asc = vals.Order().ToArray();
                    model.Band.Add(new UncertaintyBandPoint
                    {
                        SegmentId = seg.SegmentId,
                        DepthMbsf = dd,
                        Q025YrBp = Quantile(asc, .025),
                        MedianYrBp = Quantile(asc, .5),
                        Q975YrBp = Quantile(asc, .975)
                    });
                    totalWidth += Quantile(asc, .975) - Quantile(asc, .025);
                }
                spanMeters += seg.Base - seg.Top;
                coveredMeters += sa[^1].DepthMbsf - sa[0].DepthMbsf;
            }

            // 倒置层段：显式不生成速率/带，已在锚点诊断中记录
            model.CoveragePenalty = Math.Max(0, spanMeters - coveredMeters) * 50000; // 每米未覆盖 50 kyr 罚分
            model.FitScore = feasible ? totalWidth + model.CoveragePenalty : double.PositiveInfinity;
            model.Fingerprint = Hashing.Sha256(new { model.CandidateId, used = model.UsedAnchorIds, p.Id, engine = RulesetVersion });
            if (!feasible) model.ExcludedAnchorIds.Add("INFEASIBLE");
            model.CandidateId = "cand_" + code;
            results.Add(model);

            if (_clock.ElapsedTicks > p.CpuBudgetTicks) { _budgetHit = true; break; }
        }

        return RankAndMarkTies(results, p.TiedScoreEpsilon);
    }

    /// <summary>等价分数稳定排序，并列（差 ≤ ε）全部标 Tied 且按稳定身份排序。</summary>
    internal static List<CandidateModel> RankAndMarkTies(List<CandidateModel> models, double eps)
    {
        var sorted = models.OrderBy(m => m.FitScore).ThenBy(m => m.CandidateId, StringComparer.Ordinal).ToList();
        for (var i = 0; i < sorted.Count; i++)
        {
            sorted[i].Rank = i + 1;
            var tiedWith = sorted.Any(o => !ReferenceEquals(o, sorted[i]) &&
                (!double.IsInfinity(o.FitScore) || !double.IsInfinity(sorted[i].FitScore)) &&
                Math.Abs(o.FitScore - sorted[i].FitScore) <= eps * Math.Max(1, Math.Abs(sorted[i].FitScore)));
            sorted[i].Tied = tiedWith || (double.IsInfinity(sorted[i].FitScore) && sorted.Count(o => double.IsInfinity(o.FitScore)) > 1);
        }
        return sorted;
    }

    private static List<double> SampleDepths(SegmentCtx seg, List<EffectiveAnchor> anchors)
    {
        var depths = new List<double>();
        for (var i = 0; i < anchors.Count - 1; i++)
        {
            var lo = anchors[i].DepthMbsf; var hi = anchors[i + 1].DepthMbsf;
            if (hi - lo <= 0) continue;
            for (var k = 0; k <= 6; k++) depths.Add(Math.Round(lo + (hi - lo) * k / 6.0, 6));
        }
        return depths.Distinct().OrderBy(d => d).ToList();
    }

    private static double InterpAge(List<EffectiveAnchor> anchors, double[][] matrix, int draw, double depth)
    {
        for (var i = 0; i < anchors.Count - 1; i++)
        {
            if (depth < anchors[i].DepthMbsf || depth > anchors[i + 1].DepthMbsf) continue;
            var f = (depth - anchors[i].DepthMbsf) / (anchors[i + 1].DepthMbsf - anchors[i].DepthMbsf);
            return matrix[i][draw] + f * (matrix[i + 1][draw] - matrix[i][draw]);
        }
        var a = depth <= anchors[0].DepthMbsf ? anchors[0] : anchors[^1];
        return matrix[anchors.IndexOf(a)][draw];
    }
}
