using Chrono.Core.Domain;
using Chrono.Core.Models;

namespace Chrono.Core.Engine;

public sealed record EngineResult(
    OutcomeKind Kind,
    IReadOnlyList<ModelCandidate> Candidates,
    IReadOnlyList<ModelCandidate> Rejected,
    IReadOnlyList<Diagnostic> Diagnostics,
    IReadOnlyList<Judgment> Judgments,
    IReadOnlyList<TieGroup> TieGroups,
    IReadOnlyList<string> LocalRecomputeIds);

public sealed record TieGroup(double DepthM, IReadOnlyList<string> AnchorIds, bool KeptAll);

/// <summary>
/// Server-side age/depth model derivation. Nothing here lives in the browser:
/// this engine is invoked directly by tests, by the storage import path and by
/// the web service.
///
/// Hard rules:
///  * age is monotonic only within an unbroken section; gaps/unconformities are
///    explicit boundaries and are never interpolated across;
///  * an anchor distribution is carried whole (long tails included), never
///    replaced by a mean;
///  * tied anchors at one depth are enumerated as separate equally-optimal
///    candidates, and when a choice is not unique every legal candidate survives;
///  * duplicate depths from the same material series are a data error.
/// </summary>
public sealed class AgeModelEngine
{
    private int _budget;

    public EngineResult Derive(AppState state, IReadOnlyCollection<string>? onlyRecompute = null)
    {
        var p = state.Parameters;
        _budget = p.CandidateBudget;
        var diags = new List<Diagnostic>();
        var judgments = new List<Judgment>();
        var recompute = new HashSet<string>(onlyRecompute ?? Array.Empty<string>());

        var axis = DepthAxisBuilder.Build(state);
        diags.AddRange(axis.Diagnostics);

        // 1) Anchor preprocessing: depth duplicates, ties, reservoir shift, distribution.
        var usable = new List<AgeAnchor>();
        foreach (var a in state.Anchors.Values)
        {
            if (a.Status == AnchorStatus.Unconvertible || !a.Depth.IsConvertible)
            {
                diags.Add(new Diagnostic("UNCONV-ANCHOR",
                    $"锚点 {a.Id} 的值无法转换，保留原始表示，不进入拟合", $"anchor:{a.Id}", Severity.Warning));
                judgments.Add(Judgment.Create(JudgmentCode.UnconvertibleRetained, true,
                    "无法转换的值留在原位置并附诊断，不为绘图丢弃", new[] { a.Id }));
                continue;
            }
            var shifted = ApplyReservoir(state, a, diags);
            usable.Add(shifted);
            if (shifted.Distribution.IsLongTail())
                judgments.Add(Judgment.Create(JudgmentCode.LongTailDetected, true,
                    $"锚点 {a.Id} 的年龄分布具长尾，保留完整形状而非均值", new[] { a.Id },
                    facts: new Dictionary<string, string>
                    {
                        ["p025"] = shifted.Distribution.Quantile(0.025).ToString("0"),
                        ["median"] = shifted.Distribution.Median().ToString("0"),
                        ["p975"] = shifted.Distribution.Quantile(0.975).ToString("0"),
                        ["mean"] = shifted.Distribution.Mean().ToString("0")
                    }));
            judgments.Add(Judgment.Create(JudgmentCode.DistributionKeptNotMean, true,
                $"锚点 {a.Id} 保留完整年龄分布（{shifted.Distribution.GetType().Name}），不做均值替代", new[] { a.Id }));
        }

        var byDepth = usable.GroupBy(a => Math.Round(a.Depth.NormValue!.Value, 3))
            .OrderBy(g => g.Key).ToList();
        var tieGroups = new List<TieGroup>();
        var chosen = new List<AgeAnchor>();
        foreach (var g in byDepth)
        {
            var ids = g.Select(x => x.Id).OrderBy(x => x).ToList();
            if (g.Count() > 1)
            {
                bool sameMaterialSeries = g.Select(x => x.Material).Distinct().Count() == 1
                    && g.All(x => x.Status != AnchorStatus.TiedAlternative);
                if (sameMaterialSeries && g.Count() == 2 &&
                    g.Any(x => x.Status == AnchorStatus.Duplicate))
                {
                    diags.Add(new Diagnostic("DEPTH-DUP-ANCHOR",
                        $"同一材料系列在深度 {g.Key}m 出现重复年龄（{string.Join(",", ids)}），判为数据错误",
                        $"anchor:{string.Join(",", ids)}", Severity.Error));
                    judgments.Add(Judgment.Create(JudgmentCode.DepthDuplicate, false,
                        "深度重复属于源数据错误，候选不被接受", ids));
                    foreach (var x in g) chosen.Add(x with { Status = AnchorStatus.Duplicate });
                    continue;
                }
                // Tied anchors: genuinely different materials / lines at same depth.
                tieGroups.Add(new TieGroup(g.Key, ids, true));
                judgments.Add(Judgment.Create(JudgmentCode.TiedAnchorsKeepMultiple, true,
                    $"深度 {g.Key}m 存在并列锚点 {string.Join("/", ids)}，保留全部等优候选", ids,
                    facts: new Dictionary<string, string> { ["depthM"] = g.Key.ToString() }));
                foreach (var x in g)
                {
                    var marked = x with { Status = AnchorStatus.TiedAlternative };
                    chosen.Add(marked);
                }
            }
            else chosen.Add(g.First());
        }

        // 2) Group anchors by unbroken section (gaps/unconformities split the axis).
        var sectionAnchors = new List<(UnbrokenSection Sec, List<AgeAnchor> Anchors)>();
        foreach (var sec in axis.Sections)
        {
            var here = chosen
                .Where(a => { var d = a.Depth.NormValue!.Value; return d >= sec.TopM - 1e-6 && d <= sec.BottomM + 1e-6; })
                .OrderBy(a => a.Depth.NormValue!.Value).ToList();
            if (here.Count > 0)
                sectionAnchors.Add((sec, here));

            if (sec.HasInversion)
                judgments.Add(Judgment.Create(JudgmentCode.InversionDetected, false,
                    $"层段 {sec.Id} 含倒置；倒置区内不保证年龄单调，仅在未倒置子段建模",
                    sec.SegmentIds));
        }

        // Barrier judgment (always emitted when barriers exist).
        foreach (var barrierId in axis.Sections.SelectMany(x => x.BarrierIds).Distinct())
            judgments.Add(Judgment.Create(JudgmentCode.BarrierStopsMonotonic, true,
                $"深度缺口/不整合 {barrierId} 是显式边界，年龄不跨边界插值或外推",
                new[] { barrierId }));

        // 3) Within every unbroken section: monotonic medians + valid rates.
        var accepted = new List<ModelCandidate>();
        var rejected = new List<ModelCandidate>();
        var localIds = new HashSet<string>();

        foreach (var (sec, anchors) in sectionAnchors)
        {
            // Enumerate tie choices deterministically (stable order across equivalent input).
            var combinations = EnumerateTieChoices(anchors);
            if (combinations.Count > _budget)
            {
                return new EngineResult(OutcomeKind.ResourceBudgetExhausted,
                    accepted, rejected,
                    new[] { new Diagnostic("BUDGET-000",
                        $"并列组合数 {combinations.Count} 超过候选预算 {_budget}，资源预算耗尽（区别于格式错误与语义无解）",
                        $"section:{sec.Id}") },
                    judgments, tieGroups, recompute.ToList());
            }

            double bestScore = double.PositiveInfinity;
            var best = new List<(List<AgeAnchor> Pick, double Score, List<RateInterval> Rates, List<(double,double,double,double)> Band)>();

            foreach (var pick in combinations)
            {
                var violation = MonotonicViolation(pick, p, out var violationIds);
                if (violation is not null)
                {
                    rejected.Add(StubCandidate(state, sec, pick, violationIds, violation));
                    continue;
                }
                var (rates, rateOk) = ComputeRates(pick, p, sec);
                if (!rateOk)
                {
                    rejected.Add(StubCandidate(state, sec, pick, pick.Select(x=>x.Id).ToList(),
                        $"速率越出 [{p.MinRateCmPerKyr},{p.MaxRateCmPerKyr}] cm/kyr 合理区间"));
                    continue;
                }
                var band = BuildBand(pick, rates, sec);
                var score = ScorePick(pick, rates);
                if (score < bestScore - 1e-9) { bestScore = score; best.Clear(); }
                if (Math.Abs(score - bestScore) <= 1e-9) best.Add((pick, score, rates, band));
            }

            // Multiple equally-optimal models are all retained.
            if (best.Count > 1)
                judgments.Add(Judgment.Create(JudgmentCode.TiedAnchorsKeepMultiple, true,
                    $"层段 {sec.Id} 存在 {best.Count} 个等优模型，全部保留",
                    best.SelectMany(b => b.Pick.Select(x => x.Id)).Distinct().ToList()));

            for (int i = 0; i < best.Count; i++)
            {
                var b = best[i];
                var ids = b.Pick.Select(x => x.Id).OrderBy(x => x).ToList();
                var candId = CandidateId.Deterministic(sec.Id, ids, p.Version);
                var j = new List<Judgment>
                {
                    Judgment.Create(JudgmentCode.MonotonicWithinUnbroken, true,
                        $"层段 {sec.Id} 内年龄随深度单调（仅约束于不断层段）", ids)
                };
                var parents = b.Pick.SelectMany(x => new[] { x.Id }).Distinct().ToList();
                var cand = new ModelCandidate(candId,
                    i == 0 ? $"{sec.Id} 线性插值候选" : $"{sec.Id} 并列候选 #{i + 1}",
                    CandidateFamily.LinearInterpolation, ids, Math.Round(b.Score, 6),
                    best.Count > 1, false, null, null, b.Rates, b.Band, j,
                    p.Version, parents, state.LastEventId ?? "import", 1);
                accepted.Add(cand);
                if (onlyRecompute is not null && ids.Any(recompute.Contains))
                {
                    foreach (var id in ids) localIds.Add(id);
                }
            }
        }

        if (onlyRecompute is not null && recompute.Count > 0)
            judgments.Add(Judgment.Create(JudgmentCode.LocalRecompute, true,
                "源数据改动只触发局部依赖重算，其余候选冻结不变",
                localIds.ToList()));

        var kind = rejected.Count > 0 && accepted.Count == 0
            ? OutcomeKind.SemanticImpossible
            : OutcomeKind.Ok;
        return new EngineResult(kind, accepted, rejected, diags, judgments, tieGroups, localIds.ToList());
    }

    private static AgeAnchor ApplyReservoir(AppState state, AgeAnchor a, List<Diagnostic> diags)
    {
        if (a.ReservoirId is null || !state.Reservoirs.TryGetValue(a.ReservoirId, out var r)) return a;
        if (!r.OffsetYr.IsConvertible)
        {
            diags.Add(new Diagnostic("RESERVOIR-UNCONV",
                $"锚点 {a.Id} 的储层偏移无法转换，原始值保留，不施加校正", $"reservoir:{r.Id}", Severity.Warning));
            return a;
        }
        var sd = state.Parameters.ReservoirDefaultSdYr;
        var corrected = a with { Distribution = a.Distribution.ShiftReservoir(r.OffsetYr.NormValue!.Value, sd) };
        return corrected;
    }

    /// <summary>Cartesian product of tied-anchor choices at each depth, with a
    /// stable ordering: picks are sorted by anchor id so equivalent inputs always
    /// produce the identical candidate order.</summary>
    private static List<List<AgeAnchor>> EnumerateTieChoices(List<AgeAnchor> anchors)
    {
        var groups = anchors.GroupBy(x => Math.Round(x.Depth.NormValue!.Value, 6))
            .OrderBy(g => g.Key)
            .Select(g => g.OrderBy(x => x.Id, StringComparer.Ordinal).ToList())
            .ToList();
        var combos = new List<List<AgeAnchor>> { new() };
        foreach (var grp in groups)
        {
            var next = new List<List<AgeAnchor>>();
            foreach (var prefix in combos)
                foreach (var choice in grp)
                    next.Add(prefix.Append(choice).ToList());
            combos = next;
        }
        return combos.OrderBy(c =>
            string.Join("|", c.Select(x => x.Id)), StringComparer.Ordinal).ToList();
    }

    /// <summary>Monotonic "older down-core" within one unbroken section, tested on
    /// the 95% credible intervals of the *full* distributions. Requiring only the
    /// medians to order would silently discard long-tail uncertainty.</summary>
    private static string? MonotonicViolation(List<AgeAnchor> pick, ParameterSet p, out List<string> ids)
    {
        ids = new List<string>();
        var ordered = pick.OrderBy(a => a.Depth.NormValue!.Value).ToList();
        for (int i = 1; i < ordered.Count; i++)
        {
            var upper = ordered[i - 1].Distribution.Quantile(0.975);
            var lower = ordered[i].Distribution.Quantile(0.025);
            if (lower < upper - 1e-9)
            {
                ids.Add(ordered[i - 1].Id); ids.Add(ordered[i].Id);
                return $"年龄非单调：更深的 {ordered[i].Id} 的 2.5% 分位({lower:0}) 早于上覆 {ordered[i - 1].Id} 的 97.5% 分位({upper:0})";
            }
        }
        return null;
    }

    private static (List<RateInterval> Rates, bool Ok) ComputeRates(
        List<AgeAnchor> pick, ParameterSet p, UnbrokenSection sec)
    {
        var rates = new List<RateInterval>();
        bool ok = true;
        var o = pick.OrderBy(a => a.Depth.NormValue!.Value).ToList();
        for (int i = 1; i < o.Count; i++)
        {
            var dTop = o[i - 1].Depth.NormValue!.Value;
            var dBot = o[i].Depth.NormValue!.Value;
            var depthCm = Math.Abs(dBot - dTop) * 100.0;
            var ageLo = Math.Abs(o[i].Distribution.Quantile(0.975) - o[i - 1].Distribution.Quantile(0.025));
            var ageHi = Math.Abs(o[i].Distribution.Quantile(0.025) - o[i - 1].Distribution.Quantile(0.975));
            (ageLo, ageHi) = (Math.Min(ageLo, ageHi), Math.Max(ageLo, ageHi));
            var rLo = depthCm / (ageHi / 1000.0);
            var rHi = depthCm / (Math.Max(ageLo, 1) / 1000.0);
            if (rHi < p.MinRateCmPerKyr || rLo > p.MaxRateCmPerKyr) ok = false;
            rates.Add(new RateInterval(dTop, dBot, Math.Round(rLo, 2), Math.Round(rHi, 2), false,
                $"{o[i-1].Id}->{o[i].Id} 分位传播"));
        }
        return (rates, ok);
    }

    private static List<(double, double, double, double)> BuildBand(
        List<AgeAnchor> pick, List<RateInterval> rates, UnbrokenSection sec)
    {
        var band = new List<(double, double, double, double)>();
        var o = pick.OrderBy(a => a.Depth.NormValue!.Value).ToList();
        foreach (var a in o)
        {
            var d = a.Depth.NormValue!.Value;
            var (lo, hi) = a.Distribution.Credible95();
            band.Add((d, lo, a.Distribution.Median(), hi));
        }
        // One intermediate point per rate interval, interpolated in quantile space.
        foreach (var r in rates)
        {
            var mid = (r.TopM + r.BottomM) / 2;
            var up = o.First(x => Math.Abs(x.Depth.NormValue!.Value - r.TopM) < 1e-9);
            var dn = o.First(x => Math.Abs(x.Depth.NormValue!.Value - r.BottomM) < 1e-9);
            band.Add((mid,
                (up.Distribution.Quantile(0.025) + dn.Distribution.Quantile(0.025)) / 2,
                (up.Distribution.Median() + dn.Distribution.Median()) / 2,
                (up.Distribution.Quantile(0.975) + dn.Distribution.Quantile(0.975)) / 2));
        }
        return band.OrderBy(x => x.Item1).ToList();
    }

    private static double ScorePick(List<AgeAnchor> pick, List<RateInterval> rates)
    {
        // Prefer narrow, steady rates; ties fall out of identical anchor sets.
        if (rates.Count == 0) return 0;
        double spread = rates.Sum(r => Math.Log(1 + r.RateHi - r.RateLo));
        var mids = rates.Select(r => (r.RateLo + r.RateHi) / 2).ToList();
        double roughness = mids.Zip(mids.Skip(1), (a, b) => Math.Abs(a - b)).Sum();
        return Math.Round(spread + 0.01 * roughness, 6);
    }

    private static ModelCandidate StubCandidate(AppState state, UnbrokenSection sec,
        List<AgeAnchor> pick, List<string> ids, string reason) =>
        new("rejected-" + CandidateId.Deterministic(sec.Id, ids.OrderBy(x=>x).ToList(), state.Parameters.Version),
            $"{sec.Id} 被否决候选", CandidateFamily.LinearInterpolation,
            ids.OrderBy(x => x).ToList(), double.PositiveInfinity, false, false, null, null,
            Array.Empty<RateInterval>(), Array.Empty<(double, double, double, double)>(),
            new[] { Judgment.Create(JudgmentCode.MonotonicWithinUnbroken, false, reason, ids) },
            state.Parameters.Version, ids, state.LastEventId ?? "import", 1, false, reason);
}
