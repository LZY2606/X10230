using System.Text;
using Chrono.Core.Domain;

namespace Chrono.Core.Engine;

/// <summary>
/// 深度-年龄建模引擎（纯领域逻辑，可被测试直接调用）。
/// 规则：年龄只能在不断层段内单调；深度缺口/不整合是显式边界；
/// 锚点保留完整分布；并列锚点枚举全部等优合法候选。
/// </summary>
public sealed class ModelEngine
{
    private readonly CalibrationParams _params;

    public ModelEngine(CalibrationParams p) => _params = p;

    public sealed record ModelInputs(
        IReadOnlyList<CoreSegment> Segments,
        IReadOnlyList<SegmentBoundary> Boundaries,
        IReadOnlyList<AgeAnchor> Anchors,
        IReadOnlyList<EventLayer> Events,
        IReadOnlyDictionary<string, ReservoirOffset> Reservoirs,
        IReadOnlyList<ExclusionDecision> Exclusions);

    public ModelBuildReport Build(ModelInputs inputs, ComputeBudget budget, out IReadOnlyList<AgeAnchor> calibratedAnchors)
    {
        var diags = new List<Diagnostic>();
        var budgetExhausted = false;

        // 1) 排除决定覆盖锚点状态（最新决定为准；原始材料永不删除）
        var status = inputs.Anchors.ToDictionary(a => a.Id, a => a.Status);
        var reasons = inputs.Anchors.ToDictionary(a => a.Id, a => a.ExclusionReason);
        foreach (var ex in inputs.Exclusions.Where(e => e.TargetKind == "anchor"))
        {
            if (status.ContainsKey(ex.TargetId))
            {
                status[ex.TargetId] = ex.Excluded ? AnchorStatus.Excluded : AnchorStatus.Accepted;
                reasons[ex.TargetId] = ex.Reason;
            }
        }

        // 2) 派生层标定（原始 AgeMeasure 保留）
        var calibrated = new List<AgeAnchor>();
        foreach (var a in inputs.Anchors)
        {
            inputs.Reservoirs.TryGetValue(a.ReservoirId, out var res);
            AgeDist? dist = null;
            if (status[a.Id] == AnchorStatus.Accepted)
            {
                dist = string.IsNullOrEmpty(a.EventLayerId)
                    ? Calibrator.Calibrate(a.Measure, res, _params)
                    : (inputs.Events.FirstOrDefault(e => e.Id == a.EventLayerId)?.ReferenceAge
                       ?? Calibrator.Calibrate(a.Measure, res, _params));
            }
            calibrated.Add(a with { Status = status[a.Id], ExclusionReason = reasons[a.Id], CalibratedDist = dist });
        }
        calibratedAnchors = calibrated;

        // 3) 显式边界（缺口/不整合）深度
        var hardBoundaries = inputs.Boundaries
            .Where(b => b.Kind is BoundaryKind.Gap or BoundaryKind.Unconformity)
            .OrderBy(b => b.Depth.Meters)
            .ToList();

        var active = calibrated
            .Where(a => a.Status == AnchorStatus.Accepted && a.CalibratedDist is not null)
            .OrderBy(a => a.Depth.Meters).ThenBy(a => a.Id, StringComparer.Ordinal)
            .ToList();

        // 4) 深度重复诊断：同深度并列锚点 → 并列组（tie group）
        var tieGroups = new List<List<AgeAnchor>>();
        var seenDepth = new Dictionary<string, List<AgeAnchor>>();
        foreach (var a in active)
        {
            var key = Fmt.Num(Math.Round(a.Depth.Meters / _params.TieToleranceMeters));
            if (!seenDepth.TryGetValue(key, out var grp))
                seenDepth[key] = grp = new List<AgeAnchor>();
            grp.Add(a);
        }
        var depthKeyOf = new Dictionary<string, string>();
        foreach (var (key, grp) in seenDepth)
        {
            foreach (var a in grp) depthKeyOf[a.Id] = key;
            if (grp.Count > 1)
            {
                tieGroups.Add(grp);
                diags.Add(new Diagnostic(OutcomeKind.SemanticImpossible, "DEPTH_TIE",
                    $"深度并列锚点 {grp.Count} 个（{grp[0].Depth.RawText}）：枚举全部等优候选，不取均值、不静默选一个",
                    grp[0].RawLine));
            }
        }

        // 5) 边界把锚点切成独立连续段
        var sections = SectionAnchors(active, hardBoundaries);

        var inputsHash = InputsHash(inputs, calibrated);

        // 6) 枚举并列选择 × 插值方法（笛卡尔积；预算耗尽单独标记）
        var tieOptions = tieGroups
            .OrderBy(g => g[0].Depth.Meters).ThenBy(g => g[0].Id, StringComparer.Ordinal)
            .Select(g => g.OrderBy(x => x.Id, StringComparer.Ordinal).ToList())
            .ToList();
        var combinations = EnumerateCombinations(tieOptions);

        var candidates = new List<DepthAgeModel>();
        var drawsBudgetWarned = false;
        foreach (var combo in combinations)
        {
            if (!budget.TryUseCandidate()) { budgetExhausted = true; break; }
            var chosen = new HashSet<string>(combo);
            var tieChoiceIds = combo.Count == 0
                ? new List<string>()
                : combo.OrderBy(x => x, StringComparer.Ordinal).ToList();
            foreach (var method in _params.InterpolationMethods)
            {
                if (!budget.TryStep()) { budgetExhausted = true; break; }
                foreach (var (sectionAnchors, sectionIndex) in sections.Select((x, i) => (x, i)))
                {
                    var sectionChosen = sectionAnchors.Where(a => chosen.Contains(a.Id) || tieGroups.All(g => !g.Any(x => x.Id == a.Id))).ToList();
                    var (model, drawExhausted) = BuildOne(sectionChosen, hardBoundaries, method, inputsHash, tieChoiceIds, budget, sectionIndex);
                    if (drawExhausted) { drawsBudgetWarned = true; }
                    if (model is not null) candidates.Add(model);
                }
            }
            if (budgetExhausted) break;
        }
        if (drawsBudgetWarned) budgetExhausted = true;

        // 7) 等优排序：合法性相同的候选全部 EqualBest；顺序稳定（方法, 锚点身份）
        var ranked = RankCandidates(candidates);

        return new ModelBuildReport(ranked, diags, budgetExhausted, tieGroups.Count, budget.SamplesUsed);
    }

    private static List<List<AgeAnchor>> SectionAnchors(List<AgeAnchor> active, List<SegmentBoundary> hard)
    {
        var result = new List<List<AgeAnchor>>();
        var current = new List<AgeAnchor>();
        var cuts = hard.Select(b => b.Depth.Meters).OrderBy(x => x).ToList();
        foreach (var a in active)
        {
            while (cuts.Count > 0 && cuts[0] < a.Depth.Meters - 1e-9)
            {
                if (current.Count > 0) { result.Add(current); current = new(); }
                cuts.RemoveAt(0);
            }
            current.Add(a);
        }
        if (current.Count > 0) result.Add(current);
        return result;
    }

    private IEnumerable<List<string>> EnumerateCombinations(List<List<AgeAnchor>> tieOptions)
    {
        if (tieOptions.Count == 0) { yield return new List<string>(); yield break; }
        var indices = new int[tieOptions.Count];
        while (true)
        {
            yield return indices.Select((idx, g) => tieOptions[g][idx].Id).ToList();
            var k = 0;
            while (k < indices.Length)
            {
                indices[k]++;
                if (indices[k] < tieOptions[k].Count) break;
                indices[k] = 0;
                k++;
            }
            if (k == indices.Length) break;
        }
    }

    private (DepthAgeModel? model, bool drawExhausted) BuildOne(
        List<AgeAnchor> anchors, List<SegmentBoundary> boundaries, string method,
        string inputsHash, List<string> tieChoices, ComputeBudget budget, int sectionIndex)
    {
        if (anchors.Count == 0) return (null, false);
        anchors = anchors.OrderBy(a => a.Depth.Meters).ThenBy(a => a.Id, StringComparer.Ordinal).ToList();

        var nDraws = _params.MonteCarloSamples;
        var drawExhausted = !budget.TryUseSamples(nDraws);
        if (drawExhausted) nDraws = Math.Max(2, Math.Min(nDraws, budget.MaxSamples - budget.SamplesUsed));
        if (nDraws < 2) return (null, true);

        var draws = new double[nDraws][];
        for (var d = 0; d < nDraws; d++)
        {
            draws[d] = new double[anchors.Count];
            for (var i = 0; i < anchors.Count; i++)
            {
                var seed = StableId.SeedFor(anchors[i].Id, $"draw|{method}|{_params.Version}") + (ulong)d;
                var rng = new DeterministicRng(seed);
                draws[d][i] = anchors[i].CalibratedDist!.Sample(rng);
            }
        }
        if (method == "isotonic")
        {
            for (var d = 0; d < nDraws; d++)
                PavaMonotonic(draws[d]);
        }

        var anchorIds = anchors.Select(a => a.Id).ToList();
        var idBase = StableId.CanonicalKey(
            ("m", method), ("pv", _params.Version), ("h", inputsHash),
            ("sec", sectionIndex.ToString()), ("ties", string.Join(",", tieChoices)));
        var id = $"model_{StableId.Fnv1a(idBase):x16}";

        // 速率区间（完整分布的分位数）
        var rates = new List<RateInterval>();
        var anyDecreasing = false;
        for (var i = 0; i + 1 < anchors.Count; i++)
        {
            var ddM = anchors[i + 1].Depth.Meters - anchors[i].Depth.Meters;
            var rateDraws = new double[nDraws];
            var neg = 0;
            for (var d = 0; d < nDraws; d++)
            {
                var ageGap = draws[d][i] - draws[d][i + 1]; // 向下年龄应更老（cal BP 更大）
                var slope = ageGap / (ddM * 1000.0); // m/ka：年龄随深度增加
                rateDraws[d] = slope;
                if (slope < 0) neg++;
            }
            Array.Sort(rateDraws);
            var decreasingFraction = (double)neg / nDraws;
            var decreasing = decreasingFraction > 0.5;
            if (decreasing) anyDecreasing = true;
            rates.Add(new RateInterval
            {
                Id = $"rate_{StableId.Fnv1a(id + "|" + anchors[i].Id + "|" + anchors[i + 1].Id):x16}",
                FromAnchorId = anchors[i].Id,
                ToAnchorId = anchors[i + 1].Id,
                FromDepth = anchors[i].Depth,
                ToDepth = anchors[i + 1].Depth,
                MedianMPerKyr = DepthAgeModel.QuantileSorted(rateDraws, 0.5),
                Q025MPerKyr = DepthAgeModel.QuantileSorted(rateDraws, 0.025),
                Q975MPerKyr = DepthAgeModel.QuantileSorted(rateDraws, 0.975),
                Decreasing = decreasing,
            });
        }

        var valid = true;
        var reason = "";
        if (anyDecreasing && method != "isotonic")
        {
            valid = false;
            reason = "INVERTED_SEGMENT: 年龄在连续层段内非单调（倒置层段）；该候选保留但标记非法";
        }

        var score = valid ? 0 : 1;
        return (new DepthAgeModel
        {
            Id = id,
            Method = method,
            ParamsVersion = _params.Version,
            InputsHash = inputsHash,
            AnchorIds = anchorIds,
            TieChoices = tieChoices,
            Rates = rates,
            AnchorDepthM = anchors.Select(a => a.Depth.Meters).ToList(),
            AgeDraws = draws,
            DrawCount = nDraws,
            SegmentId = $"section_{sectionIndex}",
            Valid = valid,
            InvalidReason = reason,
            EqualBest = true,
            Score = score,
        }, drawExhausted);
    }

    private static void PavaMonotonic(double[] agesWithDepth)
    {
        // 池相邻违例者（深度向下 cal BP 年龄应不减）
        var blocks = agesWithDepth.Select(v => (v, w: 1.0)).ToList();
        for (var i = 0; i < blocks.Count - 1; i++)
        {
            while (i >= 0 && blocks[i].v > blocks[i + 1].v)
            {
                var merged = (blocks[i].v * blocks[i].w + blocks[i + 1].v * blocks[i + 1].w) / (blocks[i].w + blocks[i + 1].w);
                var w = blocks[i].w + blocks[i + 1].w;
                blocks[i] = (merged, w);
                blocks.RemoveAt(i + 1);
                if (i > 0) i--;
                else break;
            }
        }
        var k = 0;
        foreach (var (v, w) in blocks)
            for (var j = 0; j < (int)w; j++) agesWithDepth[k++] = v;
    }

    private static List<DepthAgeModel> RankCandidates(List<DepthAgeModel> models)
    {
        // 稳定排序：合法优先，再按方法、section、锚点身份；合法者全部 EqualBest
        var bestScore = models.Count == 0 ? 0 : models.Min(m => m.Score);
        return models
            .OrderBy(m => m.Score)
            .ThenBy(m => m.Method, StringComparer.Ordinal)
            .ThenBy(m => m.SegmentId, StringComparer.Ordinal)
            .ThenBy(m => string.Join(",", m.AnchorIds), StringComparer.Ordinal)
            .ThenBy(m => m.Id, StringComparer.Ordinal)
            .Select(m => m with { EqualBest = m.Score == bestScore })
            .ToList();
    }

    private static string InputsHash(ModelInputs inputs, List<AgeAnchor> calibrated)
    {
        var sb = new StringBuilder();
        foreach (var a in calibrated.OrderBy(x => x.Id, StringComparer.Ordinal))
            sb.Append(a.Id).Append('|').Append(a.Status).Append('|')
              .Append(a.Depth.Meters.ToString("R")).Append('|')
              .Append(a.CalibratedDist?.Median().ToString("R") ?? "x").Append('|')
              .Append(a.EventLayerId).Append('\n');
        foreach (var b in inputs.Boundaries.OrderBy(x => x.Id, StringComparer.Ordinal))
            sb.Append("B|").Append(b.Id).Append('|').Append(b.Kind).Append('|')
              .Append(b.Depth.Meters.ToString("R")).Append('|').Append(b.MissingMeters.ToString("R")).Append('\n');
        foreach (var e in inputs.Exclusions.OrderBy(x => x.Id, StringComparer.Ordinal))
            sb.Append("X|").Append(e.Id).Append('|').Append(e.TargetId).Append('|').Append(e.Excluded).Append('\n');
        return StableId.Fnv1a(sb.ToString()).ToString("x16");
    }
}
