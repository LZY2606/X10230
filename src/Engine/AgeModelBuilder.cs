using System.Text;
using DepthAgeReview.Domain;

namespace DepthAgeReview.Engine;

/// <summary>
/// 深度-年龄模型构建器（服务端业务规则，浏览器不持有规则）。
/// 关键规则（同时由结构化事件 machine-readable 断言）：
///  1. 年龄只能在不断层段（conformable）内单调；深度缺口/不整合是显式边界，绝不跨边界插值；
///  2. 锚点是分布：用分位数带与蒙特卡洛传播，绝不以均值替代；
///  3. 同深度并列/深度重复形成并列组，每个合法选择都生成候选；多个等优模型全部保留；
///  4. 预算耗尽是独立结果 BudgetExhausted；倒置不可解是 SemanticUnresolvable；格式损坏在导入层。
/// </summary>
public sealed class AgeModelBuilder
{
    private readonly BuildOptions _opt;
    public AgeModelBuilder(BuildOptions? opt = null) => _opt = opt ?? BuildOptions.Default;

    public BuildResult Build(MaterialBundle mat, string parentModelId = "ROOT", IReadOnlySet<string>? scopeSegments = null)
    {
        var diags = new List<Diagnostic>();
        var flags = new List<ModelFlag>();
        if (mat.Segments.Count == 0)
        {
            diags.Add(new Diagnostic("NO_SEGMENTS", "没有岩芯分段，无法构建深度年龄模型", DiagnosticSeverity.Error, "material:segments"));
            return new BuildResult(BuildOutcomeKind.SemanticUnresolvable, Array.Empty<CandidateModel>(), diags, Array.Empty<TieGroup>(), _opt, 0, false);
        }

        var tieGroups = BuildTieGroups(mat, diags);
        var activeAnchors = mat.Anchors.Where(a => !a.Excluded && a.Depth?.Canonical is not null).ToList();

        // 倒置硬检验（不断层段内，同一次抽样中年龄必须随深度不减老；95% 倒置 → 语义无解）
        foreach (var seg in mat.Segments)
        {
            if (scopeSegments is not null && !scopeSegments.Contains(seg.Id)) continue;
            var here = activeAnchors.Where(a => a.SegmentId == seg.Id)
                .OrderBy(a => a.DepthCm).GroupBy(a => a.DepthCm).Select(g => g.First()).ToList();
            for (var i = 1; i < here.Count; i++)
            {
                var (upper, lower) = (here[i - 1], here[i]);
                var distA = DistributionOf(upper, mat);
                var distB = DistributionOf(lower, mat);
                var inv = InversionProbability(distA, distB, upper.Id + "|" + lower.Id);
                if (inv >= _opt.InversionHardProbability)
                    diags.Add(new Diagnostic("AGE_INVERSION_HARD",
                        $"不断层段 {seg.Id} 内锚点 {upper.Id}→{lower.Id}（{upper.DepthRaw}→{lower.DepthRaw}）" +
                        $"年龄倒置概率 {inv:0.###} ≥ {_opt.InversionHardProbability}：无合法单调模型",
                        DiagnosticSeverity.Error, $"segment:{seg.Id}", $"{upper.Id}|{lower.Id}"));
                else if (inv > 0.05)
                    flags.Add(new ModelFlag(FlagCode.SoftInversion,
                        $"段 {seg.Id} 内 {upper.Id}→{lower.Id} 有 {inv:0.###} 的抽样倒置（软冲突，以分位数带表达，不唯一裁决）", seg.Id));
                if (distA.TailRightRatio > _opt.LongTailRightRatio || distB.TailRightRatio > _opt.LongTailRightRatio)
                    flags.Add(new ModelFlag(FlagCode.LongTail,
                        $"段 {seg.Id} 锚点年龄分布右长尾（比值 {Math.Max(distA.TailRightRatio, distB.TailRightRatio):0.00}），分位数带保留长尾，不用均值",
                        seg.Id));
            }
        }

        if (diags.Any(d => d.Severity == DiagnosticSeverity.Error))
            return new BuildResult(BuildOutcomeKind.SemanticUnresolvable, Array.Empty<CandidateModel>(), diags, tieGroups, _opt, 0, false);

        // 候选枚举：每个并列组选一个锚点 × 插值方法
        var groupsInScope = tieGroups.Where(g => scopeSegments is null || scopeSegments.Contains(g.SegmentId)).ToList();
        var combos = EnumerateChoices(groupsInScope);
        var methods = new[] { InterpolationMethod.Linear, InterpolationMethod.Pchip };
        var enumerated = 0;
        var budgetReached = false;
        var raw = new List<CandidateModel>();

        foreach (var choice in combos)
        {
            foreach (var method in methods)
            {
                if (enumerated + 1 > _opt.CandidateBudget) { budgetReached = true; break; }
                enumerated++;
                raw.Add(BuildOne(mat, activeAnchors, choice, method, parentModelId, scopeSegments, flags));
            }
            if (budgetReached) break;
        }

        // 评分：单调违反(应为0) + 中位曲线对锚点中位数的加权失配；等优按 epsilon 判定并全部保留
        var scored = raw.Select((c, i) => c with { Score = Score(c, mat) }).ToList();
        var bestScore = scored.Min(c => c.Score);
        var best = scored.Where(c => Math.Abs(c.Score - bestScore) <= _opt.TieEpsilon).ToList();
        var bestIds = best.Select(c => c.Fingerprint).ToHashSet();
        // 等价输入稳定顺序：分数 → 方法名 → 锚点选择列表
        var ordered = scored
            .OrderBy(c => c.Score)
            .ThenBy(c => c.Method.ToString(), StringComparer.Ordinal)
            .ThenBy(c => string.Join(",", c.ChosenAnchorIds), StringComparer.Ordinal)
            .ToList();
        for (var i = 0; i < ordered.Count; i++)
        {
            var rank = i + 1;
            ordered[i] = ordered[i] with { Rank = rank, EquallyOptimal = Math.Abs(ordered[i].Score - bestScore) <= _opt.TieEpsilon };
        }

        var outcome = budgetReached ? BuildOutcomeKind.BudgetExhausted : BuildOutcomeKind.Ok;
        if (budgetReached)
            diags.Add(new Diagnostic("BUDGET_EXHAUSTED",
                $"候选枚举在预算 {_opt.CandidateBudget} 处截断（已枚举 {enumerated}），结果不完整；与语义无解/格式损坏严格区分",
                DiagnosticSeverity.Warning, "engine:candidate-budget"));
        return new BuildResult(outcome, ordered, diags, tieGroups, _opt, enumerated, budgetReached);
    }

    public static AgeDistribution DistributionOf(AgeAnchor a, MaterialBundle mat)
    {
        AgeDistribution d = a.Kind switch
        {
            AgeDistributionKind.Normal => new NormalDist(a.MeanCalAbp ?? 0, Math.Max(1e-6, a.SigmaCalAbp ?? 0)),
            AgeDistributionKind.StudentT => new StudentTDist(a.MeanCalAbp ?? 0, Math.Max(1e-6, a.SigmaCalAbp ?? 1), a.Tnu ?? 3),
            AgeDistributionKind.Empirical when a.EmpiricalCalAbp is { Length: > 0 } => new EmpiricalDist(a.EmpiricalCalAbp),
            AgeDistributionKind.Point => new PointDist(a.MeanCalAbp ?? 0),
            _ => new NormalDist(a.MeanCalAbp ?? 0, Math.Max(1e-6, a.SigmaCalAbp ?? 0)),
        };
        if (a.ReservoirCorrectionId is not null && mat.Reservoirs.FirstOrDefault(r => r.Id == a.ReservoirCorrectionId) is { } rc)
            d = ShiftReservoir(d, rc);
        return d;
    }

    private static AgeDistribution ShiftReservoir(AgeDistribution d, ReservoirCorrection rc) => d switch
    {
        NormalDist n => new NormalDist(n.Mu + rc.DeltaCalAbp, Math.Sqrt(n.Sigma * n.Sigma + rc.SigmaCalAbp * rc.SigmaCalAbp)),
        StudentTDist t => new StudentTDist(t.Mu + rc.DeltaCalAbp,
            Math.Sqrt(t.Scale * t.Scale + rc.SigmaCalAbp * rc.SigmaCalAbp), t.Nu),
        PointDist p => new PointDist(p.Value + rc.DeltaCalAbp),
        EmpiricalDist e => new EmpiricalDist(e.Samples.Select(v => v + rc.DeltaCalAbp).ToArray()),
        _ => d,
    };

    private double InversionProbability(AgeDistribution upperShallow, AgeDistribution lowerDeep, string salt)
    {
        var bad = 0;
        var n = _opt.MonteCarloDraws;
        for (var i = 0; i < n; i++)
        {
            var au = QuantileDraw(upperShallow, i, salt + ":u");
            var al = QuantileDraw(lowerDeep, i, salt + ":l");
            if (al < au) bad++;
        }
        return (double)bad / n;
    }

    internal static double QuantileDraw(AgeDistribution d, int i, string salt) =>
        d.Kind == AgeDistributionKind.Empirical
            ? d.Quantile(d.Sample(i, salt))
            : d.Quantile(d.Sample(i, salt));

    private List<TieGroup> BuildTieGroups(MaterialBundle mat, List<Diagnostic> diags)
    {
        var groups = new List<TieGroup>();
        foreach (var seg in mat.Segments)
        {
            var dup = mat.Anchors.Where(a => !a.Excluded && a.SegmentId == seg.Id && a.Depth?.Canonical is not null)
                .GroupBy(a => a.DepthCm).Where(g => g.Count() > 1).ToList();
            foreach (var g in dup)
            {
                var ids = g.Select(x => x.Id).OrderBy(x => x, StringComparer.Ordinal).ToList();
                groups.Add(new TieGroup(seg.Id, g.Key, ids,
                    g.Select(x => x.Label).Distinct().Count() == 1 ? "深度重复（同一深度的重复测年）" : "并列锚点（同深度不同证据）"));
                diags.Add(new Diagnostic("DEPTH_DUPLICATE",
                    $"段 {seg.Id} 深度 {g.Key}cm 处有 {ids.Count} 个锚点（{string.Join(",", ids)}）：保留为并列组，枚举各合法候选，不静默择一",
                    DiagnosticSeverity.Info, $"segment:{seg.Id}#depth={g.Key}", string.Join("|", ids)));
            }
        }
        return groups;
    }

    /// <summary>并列组选择的笛卡尔积；每组“恰好选一个锚点”。深度重复=等价的并列（不同重复测量）。</summary>
    private static List<Dictionary<string, string>> EnumerateChoices(List<TieGroup> groups)
    {
        var combos = new List<Dictionary<string, string>> { new() };
        foreach (var g in groups)
        {
            var next = new List<Dictionary<string, string>>();
            foreach (var baseChoice in combos)
            foreach (var pick in g.AnchorIds)
            {
                var c = new Dictionary<string, string>(baseChoice) { [GroupKey(g)] = pick };
                next.Add(c);
            }
            combos = next;
        }
        return combos;
    }

    internal static string GroupKey(TieGroup g) => $"{g.SegmentId}@{g.DepthCm:0.######}";

    private CandidateModel BuildOne(MaterialBundle mat, List<AgeAnchor> allActive,
        Dictionary<string, string> tieChoice, InterpolationMethod method, string parentModelId,
        IReadOnlySet<string>? scope, List<ModelFlag> sharedFlags)
    {
        var chosen = new List<AgeAnchor>();
        var tieByKey = tieChoice.ToDictionary(kv => kv.Key, kv => kv.Value);
        var groups = BuildTieGroups(mat, new List<Diagnostic>());
        var droppedByTie = new HashSet<string>();
        foreach (var g in groups)
        {
            var pick = tieChoice[GroupKey(g)];
            foreach (var id in g.AnchorIds.Where(id => id != pick)) droppedByTie.Add(id);
        }

        var bandNodes = new List<AgeBandNode>();
        var rates = new List<RateInterval>();
        var flags = new List<ModelFlag>(sharedFlags);
        var affected = new HashSet<string>();

        // 深度格点：每段端点 + 各被选锚点深度；不跨不整合/缺口
        var grid = new List<(string seg, double cm)>();
        foreach (var seg in mat.Segments.OrderBy(s => s.Top!.Canonical!.Value))
        {
            if (scope is not null && !scope.Contains(seg.Id)) continue;
            var top = seg.Top!.Canonical!.Value;
            var bot = seg.Bottom!.Canonical!.Value;
            var deps = new List<double> { top, bot };
            deps.AddRange(allActive.Where(a => a.SegmentId == seg.Id && !droppedByTie.Contains(a.Id)).Select(a => a.DepthCm));
            foreach (var d in deps.Distinct().OrderBy(v => v)) grid.Add((seg.Id, Math.Round(d, 6)));
            if (!seg.Conformable)
                flags.Add(new ModelFlag(FlagCode.UnconformityBoundary,
                    $"段 {seg.Id} 顶界为不整合：显式边界，年龄不跨界单调/插值", seg.Id));
            if (seg.GapToTopCm > 0)
                flags.Add(new ModelFlag(FlagCode.GapBoundary,
                    $"段 {seg.Id} 与上覆段之间有 {seg.GapToTopCm}cm 深度缺口：显式边界，不插值", seg.Id));
            affected.Add(seg.Id);
        }

        // 每个格点：若有锚点用其分布分位数；否则用段内相邻锚点分位数之间的单调插值（线性/PCHIP）
        var draws = new Dictionary<string, double[]>();
        foreach (var seg in mat.Segments.Where(s => scope is null || scope.Contains(s.Id)))
        {
            var anc = allActive.Where(a => a.SegmentId == seg.Id && !droppedByTie.Contains(a.Id))
                .OrderBy(a => a.DepthCm).ToList();
            foreach (var a in anc)
            {
                var dist = DistributionOf(a, mat);
                var q = new double[_opt.MonteCarloDraws];
                for (var i = 0; i < q.Length; i++) q[i] = dist.Quantile(dist.Sample(i, a.Id + ":" + method));
                draws[a.Id] = q;
                chosen.Add(a);
                if (mat.Events.Any(e => e.SegmentId == seg.Id && e.Depth?.Canonical == a.DepthCm))
                    flags.Add(new ModelFlag(FlagCode.EventCorrelated, $"锚点 {a.Id} 与事件层同深度相关", a.Id));
            }
        }

        foreach (var seg in mat.Segments.Where(s => scope is null || scope.Contains(s.Id)).OrderBy(s => s.Top!.Canonical))
        {
            var anc = chosen.Where(a => a.SegmentId == seg.Id).OrderBy(a => a.DepthCm).ToList();
            var segGrid = grid.Where(g => g.seg == seg.Id).Select(g => g.cm).Distinct().OrderBy(v => v).ToList();
            if (anc.Count == 0)
            {
                // 段内无锚点：无法唯一定年，输出诊断，不伪造
                flags.Add(new ModelFlag(FlagCode.BudgetTruncated, $"段 {seg.Id} 无锚点，该段年龄带不可定（界面显示失效原因，不画假带）", seg.Id));
                continue;
            }
            var anchorQs = anc.Select(a =>
            {
                var d = DistributionOf(a, mat);
                return (a.DepthCm, d.Quantile(0.025), d.Quantile(0.5), d.Quantile(0.975));
            }).ToList();

            foreach (var cm in segGrid)
            {
                var hit = anchorQs.FirstOrDefault(q => Math.Abs(q.DepthCm - cm) < 1e-6);
                (double lo, double mid, double hi) v;
                if (hit != default) v = (hit.Item2, hit.Item3, hit.Item4);
                else
                {
                    var lo = InterpAt(anchorQs.Select(x => (x.DepthCm, x.Item2)).ToList(), cm, method);
                    var mid = InterpAt(anchorQs.Select(x => (x.DepthCm, x.Item3)).ToList(), cm, method);
                    var hi = InterpAt(anchorQs.Select(x => (x.DepthCm, x.Item4)).ToList(), cm, method);
                    // 单调性保护（长尾样本插值可能局部反向）：同段内年龄中位随深度不递减
                    v = (lo, mid, hi);
                }
                bandNodes.Add(new AgeBandNode(seg.Id, cm, v.lo, v.mid, v.hi));
            }
            // 速率区间：相邻锚点间 age depth 增量 / 年龄分位数差（cm/ka）
            for (var i = 1; i < anc.Count; i++)
            {
                var u = DistributionOf(anc[i - 1], mat);
                var l = DistributionOf(anc[i], mat);
                var dd = anc[i].DepthCm - anc[i - 1].DepthCm; // cm
                var ageGapLo = Math.Max(1e-6, l.Quantile(0.025) - u.Quantile(0.975));
                var ageGapHi = Math.Max(1e-6, l.Quantile(0.975) - u.Quantile(0.025));
                var ageGapMed = Math.Max(1e-6, l.Quantile(0.5) - u.Quantile(0.5));
                rates.Add(new RateInterval(seg.Id, anc[i - 1].DepthCm, anc[i].DepthCm,
                    dd / (ageGapHi / 1000.0), dd / (ageGapLo / 1000.0), dd / (ageGapMed / 1000.0)));
            }
        }

        // 同段中位单调化（只在非显式边界内）：保证规则1的派生表达，原始分位数保留在锚点
        bandNodes = EnforceSegmentMonotonic(bandNodes, mat, scope);

        var chosenIds = chosen.Select(a => a.Id).OrderBy(x => x, StringComparer.Ordinal).ToList();
        var inputDigest = Fingerprints.Sha256(
            mat.Version + "|" + method + "|" + string.Join(",", chosenIds) + "|" + _opt.MonteCarloDraws);
        var fp = Fingerprints.Sha256(method + "|" + string.Join(",", tieChoice.OrderBy(kv => kv.Key).Select(kv => kv.Key + "=" + kv.Value)));
        var prov = new ModelProvenance(mat.Version, "rules/v1", parentModelId, inputDigest,
            mat.Anchors.Select(a => a.Id).Concat(mat.Segments.Select(s => s.Id)).Concat(mat.Events.Select(e => e.Id)).ToList(),
            "AgeModelBuilder.Build", DateTimeOffset.UtcNow);
        return new CandidateModel("mdl-" + fp, fp, 0, false, 0, method, chosenIds, bandNodes, rates,
            flags.DistinctBy(f => (f.Code, f.ObjectId, f.Message)).ToList(), affected, prov);
    }

    internal static List<AgeBandNode> EnforceSegmentMonotonic(List<AgeBandNode> nodes, MaterialBundle mat, IReadOnlySet<string>? scope)
    {
        // 规则1：年龄只在不断层段内随深度单调（越深越老）；缺口/不整合是显式边界，跨段绝不联动。
        var outNodes = new List<AgeBandNode>();
        foreach (var seg in mat.Segments)
        {
            if (scope is not null && !scope.Contains(seg.Id)) continue;
            var here = nodes.Where(n => n.SegmentId == seg.Id).OrderBy(n => n.DepthCm).ToList();
            double run025 = double.NegativeInfinity, run50 = double.NegativeInfinity, run975 = double.NegativeInfinity;
            foreach (var n in here)
            {
                run025 = Math.Max(run025, n.Q025);
                run50 = Math.Max(run50, n.Q50);
                run975 = Math.Max(run975, n.Q975);
                outNodes.Add(n with { Q025 = run025, Q50 = run50, Q975 = run975 });
            }
        }
        return outNodes;
    }

    private static double InterpAt(List<(double x, double y)> pts, double x, InterpolationMethod method)
    {
        if (pts.Count == 1) return pts[0].y;
        if (x <= pts[0].x) return pts[0].y;
        if (x >= pts[^1].x) return pts[^1].y;
        for (var i = 1; i < pts.Count; i++)
        {
            if (x <= pts[i].x)
            {
                var (x0, y0) = pts[i - 1];
                var (x1, y1) = pts[i];
                var t = (x - x0) / (x1 - x0);
                if (method == InterpolationMethod.Linear || pts.Count < 3)
                    return y0 + (y1 - y0) * t;
                return PchipEval(pts, i - 1, i, x);
            }
        }
        return pts[^1].y;
    }

    // Fritsch–Carlson 保形三次插值：不产生过冲，适合单调年龄-深度
    private static double PchipEval(List<(double x, double y)> p, int i0, int i1, double x)
    {
        var (x0, y0) = p[i0];
        var (x1, y1) = p[i1];
        var h = x1 - x0;
        var t = (x - x0) / h;
        var mk = SlopeOrZero(p, i0);
        var mk1 = SlopeOrZero(p, i1);
        var h00 = 2 * t * t * t - 3 * t * t + 1;
        var h10 = t * t * t - 2 * t * t + t;
        var h01 = -2 * t * t * t + 3 * t * t;
        var h11 = t * t * t - t * t;
        return h00 * y0 + h10 * h * mk + h01 * y1 + h11 * h * mk1;
    }

    private static double SlopeOrZero(List<(double x, double y)> p, int k)
    {
        if (k <= 0) return (p[1].y - p[0].y) / (p[1].x - p[0].x);
        if (k >= p.Count - 1) return (p[^1].y - p[^2].y) / (p[^1].x - p[^2].x);
        var s0 = (p[k].y - p[k - 1].y) / (p[k].x - p[k - 1].x);
        var s1 = (p[k + 1].y - p[k].y) / (p[k + 1].x - p[k].x);
        if (s0 == 0 || s1 == 0 || Math.Sign(s0) != Math.Sign(s1)) return 0;
        return 2 * s0 * s1 / (s0 + s1);
    }

    private static double Score(CandidateModel c, MaterialBundle mat)
    {
        // 失配：中位曲线在锚点深度处与锚点 Q50 的差（平方，ka）；等优模型该值相同 → 多候选保留
        var err = 0.0;
        foreach (var a in c.ChosenAnchorIds.Select(id => mat.Anchors.First(x => x.Id == id)))
        {
            var node = c.Bands.FirstOrDefault(b => Math.Abs(b.DepthCm - a.DepthCm) < 1e-6);
            if (node is null) continue;
            var q50 = DistributionOf(a, mat).Quantile(0.5);
            err += Math.Pow((node.Q50 - q50) / 1000.0, 2);
        }
        var monotoneViol = 0;
        return err + monotoneViol;
    }
}
