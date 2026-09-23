using System.Text.Json;

namespace ChronoCore;

/// <summary>冻结模型与新锚点输入对比的影响报告（不改动冻结模型）。</summary>
public sealed record ImpactReport(
    string FrozenModelId,
    IReadOnlyList<Diagnostic> Diagnostics,
    IReadOnlyList<string> AffectedIntervalIds,
    double? MaxAgeShiftCalA,
    bool MonotonicPreserved,
    string NewAnchorId,
    string InputDigest);

internal sealed record NormAnchor(
    Anchor A, double Depth, AgeSamples S, double Median, double Q025, double Q975, double Q005, double Q995,
    double? ReservoirSd, string IntervalId, double TieKey, string? GroupId);

/// <summary>
/// 深度-年龄模型派生引擎：业务规则在服务端/可测试层，不在浏览器。
/// 关键规则：年龄只能在不断层段中单调；深度缺口与不整合是显式边界；
/// 一个锚点保留完整分布（不用均值）；多个等优模型全部保留；
/// 预算耗尽 / 格式损坏 / 语义无解是三种不同结果。
/// </summary>
public sealed class DerivationEngine
{
    private static readonly JsonSerializerOptions JsonOpts =
        new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };
    private const double DepthEps = 1e-6;

    public string ComputeInputDigest(ProjectState s, ParameterSet p)
    {
        var sb = new System.Text.StringBuilder();
        sb.Append("IV[");
        sb.AppendJoin(";", s.IntervalOrder.Select(i => $"{i.Id}|{i.DatumId}|{i.TopDepthRaw}|{i.BottomDepthRaw}|{i.Ord}"));
        sb.Append("]GAP[");
        sb.AppendJoin(";", s.GapOrder.Select(g => $"{g.Id}|{g.Type}|{g.DatumId}|{g.AboveDepthRaw}|{g.BelowDepthRaw}|{g.Ord}"));
        sb.Append("]AN[");
        sb.AppendJoin(";", s.AnchorOrder.Select(a =>
            $"{a.Id}|{a.DepthRaw}|{a.DatumId}|{a.Ord}|{a.AlternativeGroupId}|{a.ReservoirOffsetId}|" +
            $"{a.EventLayerId}|n={a.Samples.N}|q025={Math.Round(a.Samples.Q025, 3):R}|" +
            $"q975={Math.Round(a.Samples.Q975, 3):R}|lt={a.Samples.LongTail}|df={a.Samples.DegreesOfFreedom}|" +
            (a.Samples.N > 80 ? "grid" : string.Join(",", a.Samples.SamplesCalABP.Select(v => v.ToString("R"))))));
        sb.Append("]R[");
        sb.AppendJoin(";", s.ReservoirOrder.Select(r => $"{r.Id}|{r.DeltaRRaw}|{r.UncertaintyRaw}|{r.Apply}|{r.Ord}"));
        sb.Append("]EV[");
        sb.AppendJoin(";", s.EventOrder.Select(e => $"{e.Id}|{e.DepthRaw}|{e.CorrelationKey}|{e.ExternalAgeCalABPRaw}|{e.Ord}"));
        sb.Append("]EX[");
        sb.AppendJoin(";", s.ExclusionOrder.Select(e => $"{e.Id}|{e.MaterialId}|{e.Ord}"));
        sb.Append("]P=").Append(JsonSerializer.Serialize(p, JsonOpts));
        return Hashing.Sha256(sb.ToString());
    }

    public DeriveOutcome Derive(ProjectState state, ParameterSet parameters,
        IReadOnlyCollection<string>? recomputeOnlyIntervalIds = null)
    {
        var digest = ComputeInputDigest(state, parameters);
        var parents = new List<string> { "digest:" + digest, "params:" + parameters.Version };

        var semanticDiags = new List<Diagnostic>();
        var blocks = NormalizeIntervals(state, semanticDiags);
        if (blocks is null)
            return DeriveOutcome.Fail(OutcomeKind.SemanticallyImpossible, semanticDiags[0],
                digest, parameters.Version, parents, parameters.MaxCandidates, 0) with
            { Diagnostics = semanticDiags };

        var anchors = NormalizeAnchors(state, blocks, parameters, semanticDiags);
        // 深度重复（未声明并列）、倒置层段等是硬错误 → 语义无解，不是静默成功
        var hardErrors = semanticDiags.Where(d => d.Severity == DiagSeverity.Error).ToList();
        if (hardErrors.Count > 0)
            return DeriveOutcome.Fail(OutcomeKind.SemanticallyImpossible, hardErrors[0],
                digest, parameters.Version, parents, parameters.MaxCandidates, 0) with
            { Diagnostics = semanticDiags };

        var candidateSets = EnumerateCandidatePickSets(anchors);
        if (candidateSets.Count > parameters.MaxCandidates)
            return DeriveOutcome.Fail(OutcomeKind.BudgetExhausted,
                new Diagnostic("D-BUDGET",
                    $"候选枚举需要 {candidateSets.Count} 套，超过资源预算 {parameters.MaxCandidates}；" +
                    "拒绝静默截断，调整 MaxCandidates 或并列锚点组后重试",
                    null, "maxCandidates", "derivation", DiagSeverity.Error),
                digest, parameters.Version, parents, parameters.MaxCandidates, candidateSets.Count)
                with { Diagnostics = semanticDiags };

        var models = new List<DerivedModel>();
        foreach (var interpolation in new[] { parameters.Interpolation, Alt(parameters.Interpolation) })
        {
            if (interpolation is not ("pchip" or "linear"))
                return DeriveOutcome.Fail(OutcomeKind.FormatCorrupted,
                    new Diagnostic("D-BADPARAM", $"未知插值法 '{parameters.Interpolation}'",
                        null, "interpolation", "parameters"),
                    digest, parameters.Version, parents);
            var rank0 = models.Count;
            foreach (var (pickSet, idx) in candidateSets.Select((p, i) => (p, i)))
            {
                models.Add(BuildModel(state, parameters, blocks, anchors, pickSet,
                    interpolation, digest, parents,
                    recomputeOnlyIntervalIds, semanticDiags, rank0 + idx + 1, candidateSets.Count));
            }
        }

        AssignRanks(models);
        var ties = models.GroupBy(m => Math.Round(m.FitScore, 6)).Where(g => g.Count() > 1).SelectMany(g => g).ToHashSet();
        models = models.Select(m => ties.Contains(m) ? m with { Tie = true } : m)
            .OrderBy(m => m.Rank).ToList();
        return new DeriveOutcome(OutcomeKind.Success, models, semanticDiags, digest,
            parameters.Version, parents, parameters.MaxCandidates, models.Count);
    }

    private static string Alt(string interp) => interp == "linear" ? "pchip" : "linear";

    internal sealed record IntervalBlock(CoreInterval Interval, double Top, double Bottom,
        List<NormAnchor> Anchors);

    private List<IntervalBlock>? NormalizeIntervals(ProjectState s, List<Diagnostic> diags)
    {
        if (s.IntervalOrder.Count == 0)
        {
            diags.Add(new Diagnostic("D-NO-INTERVAL", "没有任何连续取心段，无法构建深度-年龄模型",
                null, null, "intervals", DiagSeverity.Error));
            return null;
        }
        var blocks = new List<IntervalBlock>();
        foreach (var iv in s.IntervalOrder)
        {
            var top = iv.Top; var bot = iv.Bottom;
            if (!top.IsConvertible)
                diags.Add(new Diagnostic(top.DiagnosticCode ?? "Q-DEPTH",
                    $"段 {iv.Id} 顶深不可转换：{top.Diagnostic}（保留原位，不用于绘图/建模）",
                    iv.Id, "topDepthRaw", iv.TopDepthRaw));
            if (!bot.IsConvertible)
                diags.Add(new Diagnostic(bot.DiagnosticCode ?? "Q-DEPTH",
                    $"段 {iv.Id} 底深不可转换：{bot.Diagnostic}（保留原位）",
                    iv.Id, "bottomDepthRaw", iv.BottomDepthRaw));
            if (top.IsConvertible && bot.IsConvertible)
            {
                if (top.Value!.Value >= bot.Value!.Value - DepthEps)
                    diags.Add(new Diagnostic("D-INVERTED-INTERVAL",
                        $"倒置层段：{iv.Id} 顶深 {top.Display} 不小于底深 {bot.Display}，段几何无意义",
                        iv.Id, "bottomDepthRaw", $"{iv.TopDepthRaw}..{iv.BottomDepthRaw}"));
                else
                    blocks.Add(new IntervalBlock(iv, top.Value.Value, bot.Value.Value, new()));
            }
        }
        return blocks;
    }

    private List<NormAnchor> NormalizeAnchors(ProjectState s, List<IntervalBlock> blocks,
        ParameterSet p, List<Diagnostic> diags)
    {
        var usable = new List<NormAnchor>();
        // 事件层（火山灰等时面）带外部年龄时，作为年龄约束伪锚点参与，仍保留完整相关来源
        var effectiveAnchors = new List<Anchor>(s.AnchorOrder);
        foreach (var ev in s.EventOrder)
        {
            if (ev.ExternalAgeCalABPRaw is null) continue;
            var ageQ = Quantity.Age(ev.ExternalAgeCalABPRaw);
            if (!ageQ.IsConvertible)
            {
                diags.Add(new Diagnostic(ageQ.DiagnosticCode ?? "Q-AGE",
                    $"事件层 {ev.Id} 外部年龄不可转换：{ageQ.Diagnostic}（保留原位）",
                    ev.Id, "externalAgeCalABPRaw", ev.ExternalAgeCalABPRaw, DiagSeverity.Warning));
                continue;
            }
            effectiveAnchors.Add(new Anchor("evanc-" + ev.Id, ev.Name + "(事件等时面)",
                ev.DepthRaw, ev.DatumId, SamplesFactory.Normal(ageQ.Value!.Value, ageQ.Value.Value * 0.02),
                "event-correlation", 500 + ev.Ord, null, ev.Id, null,
                "来自事件层相关键 " + ev.CorrelationKey));
        }
        foreach (var an in effectiveAnchors.OrderBy(a => a.Ord))
        {
            if (s.Exclusions.ContainsKey("exc-" + an.Id) ||
                s.Exclusions.Values.Any(e => e.MaterialId == an.Id))
            {
                diags.Add(new Diagnostic("D-EXCLUDED", $"锚点 {an.Id} 已按排除决定保留但不参与建模",
                    an.Id, null, null, DiagSeverity.Info));
                continue;
            }
            var dq = Quantity.Depth(an.DepthRaw, an.DatumId);
            if (!dq.IsConvertible)
            {
                diags.Add(new Diagnostic(dq.DiagnosticCode ?? "Q-DEPTH",
                    $"锚点 {an.Id} 深度不可转换：{dq.Diagnostic}；该锚点留在原位置，绝不为画图丢弃",
                    an.Id, "depthRaw", an.DepthRaw));
                continue;
            }
            if (an.Samples.N == 0)
            {
                diags.Add(new Diagnostic("D-EMPTY-DISTRIBUTION",
                    $"锚点 {an.Id} 年龄分布为空：输入不完整，不能用默认年龄伪装成功",
                    an.Id, "samples", null, DiagSeverity.Error));
                continue;
            }
            var depth = dq.Value!.Value;
            var block = blocks.FirstOrDefault(b => depth >= b.Top - DepthEps && depth <= b.Bottom + DepthEps);
            if (block is null)
            {
                diags.Add(new Diagnostic("D-ANCHOR-OUT-OF-SECTION",
                    $"锚点 {an.Id} 深度 {dq.Display} 不落在任何连续段内（可能位于显式缺口/不整合边界）",
                    an.Id, "depthRaw", an.DepthRaw, DiagSeverity.Warning));
                continue;
            }

            var samples = an.Samples;
            double? rSd = null;
            if (p.UseReservoirCorrection && an.ReservoirOffsetId is not null
                && s.Reservoirs.TryGetValue(an.ReservoirOffsetId, out var r) && r.Apply)
            {
                var dr = Quantity.Offset(r.DeltaRRaw); var du = Quantity.Offset(r.UncertaintyRaw);
                if (dr.IsConvertible && du.IsConvertible)
                {
                    var shift = dr.Value!.Value;
                    samples = new AgeSamples
                    {
                        DistributionType = samples.DistributionType,
                        DegreesOfFreedom = samples.DegreesOfFreedom,
                        LongTail = samples.LongTail,
                        SamplesCalABP = samples.SamplesCalABP.Select(x => x - shift).ToArray()
                    };
                    rSd = du.Value!.Value;
                }
            }
            usable.Add(new NormAnchor(an, depth, samples,
                samples.Median, samples.Q025, samples.Q975, samples.Q005, samples.Q995,
                rSd, block.Interval.Id, depth, an.AlternativeGroupId));
            block.Anchors.Add(usable[^1]);
        }

        // 深度重复：同一段内同一深度必须显式声明为并列（AlternativeGroupId），否则语义无解
        foreach (var b in blocks)
        {
            var byDepth = b.Anchors.GroupBy(x => Math.Round(x.Depth, 6));
            foreach (var g in byDepth.Where(g => g.Count() > 1))
            {
                var groups = g.Select(x => x.GroupId).Distinct().ToList();
                if (groups.Any(x => x is null) || groups.Count != 1)
                    diags.Add(new Diagnostic("D-DEPTH-DUPLICATE",
                        $"深度重复：段 {b.Interval.Id} 在 {g.Key:0.###} m 有 {g.Count()} 个未声明并列的锚点" +
                        "（{0}）；需用同一 AlternativeGroupId 显式标记为并列候选"
                            .Replace("{0}", string.Join(",", g.Select(x => x.A.Id))),
                        string.Join("+", g.Select(x => x.A.Id)), "depthRaw",
                        $"{g.First().A.DepthRaw}", DiagSeverity.Error));
            }
            // 段内单调（中位数非减；向下变老）。并列锚点跨候选比较，不构成单候选内部倒置
            var flat = b.Anchors.GroupBy(x => x.GroupId is null ? "solo-" + x.A.Id : x.GroupId)
                .Select(gr => gr.OrderBy(x => x.A.Id).First()).OrderBy(x => x.Depth).ToList();
            for (var i = 1; i < flat.Count; i++)
                if (flat[i].Median + DepthEps < flat[i - 1].Median)
                    diags.Add(new Diagnostic("D-NONMONOTONIC",
                        $"段 {b.Interval.Id} 内年龄倒置：{flat[i - 1].A.Id}@{flat[i - 1].Depth:0.###}m=" +
                        $"{flat[i - 1].Median:0} cal a BP 老于下方 {flat[i].A.Id}@{flat[i].Depth:0.###}m=" +
                        $"{flat[i].Median:0} cal a BP；年龄只能在不断层段中单调",
                        flat[i].A.Id, "samples", null, DiagSeverity.Error));
        }
        return usable;
    }

    /// <summary>
    /// 并列锚点组（同深度的等优解释）产生全部合法组合：无法唯一决定时保留全部候选，
    /// 而不是选一个均值或随便挑一个。
    /// </summary>
    private List<List<NormAnchor>> EnumerateCandidatePickSets(List<NormAnchor> anchors)
    {
        var byInterval = anchors.GroupBy(a => a.IntervalId);
        var combos = new List<List<NormAnchor>> { new() };
        foreach (var grp in byInterval.OrderBy(g => g.Key))
        {
            var solo = grp.Where(a => a.GroupId is null).ToList();
            var altGroups = grp.Where(a => a.GroupId is not null)
                .GroupBy(a => a.GroupId!).OrderBy(g => g.Key).ToList();

            var options = new List<List<NormAnchor>> { new(solo) };
            foreach (var alt in altGroups)
            {
                var next = new List<List<NormAnchor>>();
                foreach (var pick in alt.OrderBy(a => a.A.Id))
                foreach (var opt in options)
                    next.Add(new List<NormAnchor>(opt) { pick });
                options = next;
            }
            var expanded = new List<List<NormAnchor>>();
            foreach (var c in combos)
            foreach (var opt in options)
            {
                var merged = new List<NormAnchor>(c);
                merged.AddRange(opt);
                expanded.Add(merged);
            }
            combos = expanded;
        }
        // 稳定顺序：按所选锚点 Id 序列
        return combos
            .Select(c => c.OrderBy(a => a.IntervalId).ThenBy(a => a.Depth).ThenBy(a => a.A.Id).ToList())
            .OrderBy(c => string.Join("|", c.Select(a => a.A.Id)))
            .ToList();
    }

    private DerivedModel BuildModel(ProjectState state, ParameterSet p, List<IntervalBlock> blocks,
        List<NormAnchor> all, List<NormAnchor> pickSet, string interpolation,
        string digest, List<string> parents, IReadOnlyCollection<string>? localOnly,
        List<Diagnostic> globalDiags, int rank, int pickCount)
    {
        var pickedIds = pickSet.Select(a => a.A.Id).OrderBy(x => x).ToList();
        var idSource = digest + "|" + p.Version + "|" + interpolation + "|" + string.Join(",", pickedIds);
        var id = "mdl-" + Hashing.Sha256(idSource)[..12];

        var rates = new List<RateSegment>();
        var envelope = new List<EnvelopePoint>();
        var violations = new List<Diagnostic>(globalDiags.Where(d => d.Severity == DiagSeverity.Warning));
        var fitPenalty = 0.0;
        var touchedIntervals = new HashSet<string>();

        foreach (var b in blocks)
        {
            var pts = pickSet.Where(a => a.IntervalId == b.Interval.Id)
                .OrderBy(a => a.Depth).ThenBy(a => a.A.Id).ToList();
            if (pts.Count < 2)
            {
                violations.Add(new Diagnostic("M-UNDERCONSTRAINED",
                    $"段 {b.Interval.Id} 可建模锚点不足 2 个，该段无法确定速率（保留其余段结果）",
                    b.Interval.Id, null, null, DiagSeverity.Warning));
                continue;
            }
            touchedIntervals.Add(b.Interval.Id);
            var ds = pts.Select(a => a.Depth).ToArray();
            var med = pts.Select(a => a.Median).ToArray();
            var lo = pts.Select(a => a.Q025).ToArray();
            var hi = pts.Select(a => a.Q975).ToArray();
            var lo99 = pts.Select(a => a.Q005).ToArray();
            var hi99 = pts.Select(a => a.Q995).ToArray();
            var slopes = interpolation == "pchip" ? Pchip.Slopes(ds, med) : null;

            for (var i = 1; i < pts.Count; i++)
            {
                var dd = ds[i] - ds[i - 1];
                var dAgeLow = lo[i] - hi[i - 1]; // 最小年龄差
                var dAgeHigh = hi99[i] - lo99[i - 1];
                var dAgeMed = med[i] - med[i - 1];
                var rateValid = dd > DepthEps && dAgeLow >= -DepthEps;
                if (!rateValid)
                {
                    violations.Add(new Diagnostic("M-RATE-INVALID",
                        $"段 {b.Interval.Id} 锚点 {pts[i - 1].A.Id}→{pts[i].A.Id} 的 95% 年龄区间重叠，" +
                        "速率下界非正，候选在该段不可采纳",
                        b.Interval.Id, pts[i].A.Id, null, DiagSeverity.Warning));
                    fitPenalty += 1e6;
                }
                rates.Add(new RateSegment(b.Interval.Id, pts[i - 1].A.Id, pts[i].A.Id,
                    ds[i - 1], ds[i],
                    rateValid ? dd / Math.Max(dAgeHigh, 1e-9) : 0,
                    rateValid ? dd / Math.Max(dAgeLow, 1e-9) : 0,
                    dd / Math.Max(dAgeMed, 1e-9), rateValid));
            }

            // 不确定度带：每个锚点保留整条分位轨迹，绝不退化为均值点
            var steps = Math.Max(8, (int)Math.Ceiling((b.Bottom - b.Top) * 20));
            for (var k = 0; k <= steps; k++)
            {
                var z = b.Top + (b.Bottom - b.Top) * k / steps;
                double m, l95, h95, l99, h99;
                if (interpolation == "pchip")
                {
                    m = Pchip.Interpolate(ds, med, slopes!, z);
                    l95 = Pchip.Interpolate(ds, lo, slopes!, z);
                    h95 = Pchip.Interpolate(ds, hi, slopes!, z);
                    l99 = Pchip.Interpolate(ds, lo99, slopes!, z);
                    h99 = Pchip.Interpolate(ds, hi99, slopes!, z);
                }
                else
                {
                    m = Pchip.Linear(ds, med, z);
                    l95 = Pchip.Linear(ds, lo, z);
                    h95 = Pchip.Linear(ds, hi, z);
                    l99 = Pchip.Linear(ds, lo99, z);
                    h99 = Pchip.Linear(ds, hi99, z);
                }
                var rs = pts.Select(a => a.ReservoirSd ?? 0).DefaultIfEmpty(0).Max();
                envelope.Add(new EnvelopePoint(b.Interval.Id, Math.Round(z, 4),
                    Math.Round(m, 1), Math.Round(l95 - rs, 1), Math.Round(h95 + rs, 1),
                    Math.Round(l99 - rs, 1), Math.Round(h99 + rs, 1)));
            }
        }

        var valid = violations.All(v => v.Code != "M-RATE-INVALID")
            && rates.Count > 0 && rates.All(r => r.Valid);
        var notes = localOnly is null ? "全量重算"
            : $"局部重算，受影响段：{string.Join(",", touchedIntervals.Where(localOnly.Contains))}；" +
              $"未改动段保持父模型：{string.Join(",", parents)}";

        return new DerivedModel(id,
            "fam-" + digest[..8],
            $"候选#{rank}（{interpolation}/选取 {pickedIds.Count} 锚点）",
            rank, fitPenalty + 1.0 / (1 + rates.Count(r => r.Valid)) + (interpolation == "pchip" ? 0 : 1e-12),
            false, pickedIds,
            rates.OrderBy(r => r.IntervalId).ThenBy(r => r.DepthFromM).ToList(),
            envelope, violations, digest, p.Version, parents, valid, notes);
    }

    private static void AssignRanks(List<DerivedModel> models)
    {
        var ordered = models
            .OrderByDescending(m => m.SemanticallyValid)
            .ThenBy(m => m.FitScore)
            .ThenBy(m => m.Notes.Contains("pchip") ? 0 : 1)
            .ThenBy(m => string.Join(",", m.ChosenAnchorIds))
            .ToList();
        for (var i = 0; i < ordered.Count; i++)
        {
            var idx = models.IndexOf(ordered[i]);
            models[idx] = models[idx] with { Rank = i + 1 };
        }
    }

    /// <summary>
    /// 在已冻结模型上叠加一个“试验性”新锚点，比较影响；不修改冻结模型本身。
    /// 返回受影响段、最大年龄偏移与单调性是否保持。
    /// </summary>
    public ImpactReport CompareNewAnchor(ProjectState state, DerivedModel frozen,
        Anchor trialAnchor, ReservoirOffset? trialReservoir, ParameterSet p)
    {
        var diags = new List<Diagnostic>();
        var trial = new ProjectState
        {
            ProjectId = state.ProjectId, Name = state.Name, BranchId = state.BranchId,
            HeadSeq = state.HeadSeq, CurrentParameterVersion = state.CurrentParameterVersion
        };
        foreach (var kv in state.Datums) trial.Datums[kv.Key] = kv.Value;
        trial.DatumOrder.AddRange(state.DatumOrder);
        foreach (var kv in state.Intervals) trial.Intervals[kv.Key] = kv.Value;
        trial.IntervalOrder.AddRange(state.IntervalOrder);
        foreach (var kv in state.Gaps) trial.Gaps[kv.Key] = kv.Value;
        trial.GapOrder.AddRange(state.GapOrder);
        foreach (var kv in state.Anchors) trial.Anchors[kv.Key] = kv.Value;
        trial.AnchorOrder.AddRange(state.AnchorOrder);
        foreach (var kv in state.Reservoirs) trial.Reservoirs[kv.Key] = kv.Value;
        trial.ReservoirOrder.AddRange(state.ReservoirOrder);
        foreach (var kv in state.Events) trial.Events[kv.Key] = kv.Value;
        trial.EventOrder.AddRange(state.EventOrder);
        foreach (var kv in state.Exclusions) trial.Exclusions[kv.Key] = kv.Value;
        trial.ExclusionOrder.AddRange(state.ExclusionOrder);
        foreach (var kv in state.Parameters) trial.Parameters[kv.Key] = kv.Value;
        trial.ParameterOrder.AddRange(state.ParameterOrder);
        trial.Anchors[trialAnchor.Id] = trialAnchor;
        trial.AnchorOrder.Add(trialAnchor);
        if (trialReservoir is not null) { trial.Reservoirs[trialReservoir.Id] = trialReservoir; }

        var outcome = Derive(trial, p);
        diags.AddRange(outcome.Diagnostics);
        var best = outcome.Candidates.OrderByDescending(m => m.SemanticallyValid)
            .ThenBy(m => m.FitScore).FirstOrDefault();

        var affected = new HashSet<string>();
        double? maxShift = null;
        var mono = best?.SemanticallyValid == true;
        if (best is not null)
        {
            var frozenPts = frozen.Envelope.ToDictionary(e => e.StableId);
            foreach (var ep in best.Envelope)
            {
                // 新锚点只影响其所在连续段——缺口/不整合之外的段不重算
                var frozenP = frozen.Envelope.FirstOrDefault(f =>
                    f.IntervalId == ep.IntervalId && Math.Abs(f.DepthM - ep.DepthM) < 1e-6);
                if (frozenP is null) continue;
                var shift = Math.Abs(ep.MedianAge - frozenP.MedianAge);
                if (shift > 1e-6)
                {
                    affected.Add(ep.IntervalId);
                    maxShift = Math.Max(maxShift ?? 0, shift);
                }
            }
            if (!affected.Any() && !mono)
                diags.Add(new Diagnostic("I-NO-VALID-CANDIDATE",
                    "加入新锚点后不存在合法候选（可能造成段内倒置或深度重复）",
                    trialAnchor.Id, "samples", null, DiagSeverity.Error));
        }
        return new ImpactReport(frozen.Id, diags, affected.OrderBy(x => x).ToList(),
            maxShift, mono, trialAnchor.Id, outcome.InputDigest);
    }
}
