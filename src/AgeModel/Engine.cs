namespace AgeModel;

/// <summary>
/// 深度-年龄推导引擎（纯函数，服务端与测试直接调用）。
/// 规则：年龄只在不断层段内单调；缺口/不整合是显式边界；
/// 锚点分布不被均值替代；等优候选全部保留；等价输入得到稳定顺序。
/// </summary>
public static class Engine
{
    public sealed record DerivationResult(
        List<SegmentInfo> Segments,
        List<ModelCandidate> Candidates,
        List<Diagnostic> Diagnostics);

    public sealed record SegmentInfo(
        int Index, double FromDepthM, double ToDepthM, string? LowerBoundary,
        List<string> AnchorIds, bool Inverted, string Status);

    private sealed record Anchor(RadioAge Age, double[] CorrectedSamplesKa);

    public static Outcome<DerivationResult> Derive(
        string coreId, IReadOnlyList<Material> materials, ParamSet p, long atSeq)
    {
        var diags = new List<Diagnostic>();
        var budget = p.DerivationBudget;

        void Spend(int n)
        {
            budget -= n;
            if (budget < 0) throw new BudgetExhausted();
        }

        try
        {
            var datums = materials.OfType<DepthDatum>().Where(d => d.CoreId == coreId).ToList();
            var gaps = materials.OfType<SegmentGap>().Where(g => g.CoreId == coreId).OrderBy(g => g.FromDepthM).ToList();
            var ages = materials.OfType<RadioAge>().Where(a => a.CoreId == coreId).ToList();
            var offsets = materials.OfType<ReservoirOffset>().Where(o => o.CoreId == coreId).ToDictionary(o => o.Id);
            var exclusions = materials.OfType<ExclusionRecord>().Select(e => e.TargetId).ToHashSet();
            var events = materials.OfType<EventLayer>().Where(e => e.CoreId == coreId).ToList();

            if (datums.Count == 0)
                diags.Add(new("missing-datum", $"core {coreId} has no depth datum; cannot normalize depths", null, null, "error"));

            // 无法转换/不完整的值留在原位置并附诊断，不用空值伪装成功
            var usable = new List<RadioAge>();
            foreach (var a in ages.OrderBy(a => a.DepthM).ThenBy(a => a.Id, StringComparer.Ordinal))
            {
                Spend(1);
                if (exclusions.Contains(a.Id)) { diags.Add(new("excluded", $"anchor {a.Id} excluded by recorded decision", a.Id, a.DepthM, "info")); continue; }
                if (a.SamplesKa.Length == 0) { diags.Add(new("empty-distribution", $"anchor {a.Id} has empty age distribution; left in place, not used", a.Id, a.DepthM, "error")); continue; }
                if (a.DepthM < 0) { diags.Add(new("bad-depth", $"anchor {a.Id} has negative depth; left in place, not used", a.Id, a.DepthM, "error")); continue; }
                usable.Add(a);
            }

            // 深度重复：诊断并保留全部，由并列锚点逻辑产出多候选
            foreach (var grp in usable.GroupBy(a => a.DepthM).Where(g => g.Count() > 1))
                diags.Add(new("duplicate-depth",
                    $"depth {grp.Key} m has {grp.Count()} anchors: {string.Join(",", grp.Select(a => a.Id))}; all kept, candidates enumerated",
                    null, grp.Key, "warning"));

            // 储层偏移应用到分布样本上（不是均值）
            var anchors = usable.Select(a =>
            {
                var off = a.ReservoirOffsetId is not null && offsets.TryGetValue(a.ReservoirOffsetId, out var o) ? o.OffsetYears / 1000.0 : 0.0;
                return new Anchor(a, a.SamplesKa.Select(s => s - off).OrderBy(x => x).ToArray());
            }).ToList();

            // 显式边界：缺口与不整合切分段
            var boundaries = gaps.SelectMany(g => new[] { g.FromDepthM, g.ToDepthM }).Distinct().OrderBy(x => x).ToList();
            var allDepths = anchors.Select(a => a.Age.DepthM).Concat(events.Select(e => e.DepthM)).ToList();
            if (allDepths.Count == 0 && anchors.Count == 0)
                return Outcome<DerivationResult>.Fail(OutcomeStatus.Diagnostics,
                    diags.Append(new Diagnostic("no-input", "no usable anchors or event layers", null, null, "error")).ToArray());

            var lo = allDepths.DefaultIfEmpty(0).Min();
            var hi = allDepths.DefaultIfEmpty(0).Max();
            var cuts = new List<double> { lo };
            cuts.AddRange(boundaries.Where(b => b > lo && b < hi));
            cuts.Add(hi);

            var segments = new List<SegmentInfo>();
            var candidates = new List<ModelCandidate>();
            var semanticallyBroken = new List<string>();

            for (var i = 0; i < cuts.Count - 1; i++)
            {
                Spend(10);
                var (from, to) = (cuts[i], cuts[i + 1]);
                var gapHere = gaps.FirstOrDefault(g => g.FromDepthM >= from && g.ToDepthM <= to && g.FromDepthM >= from && to <= g.ToDepthM);
                var inGap = gaps.Any(g => from >= g.FromDepthM && to <= g.ToDepthM);
                var lowerBoundary = gaps.FirstOrDefault(g => Math.Abs(g.ToDepthM - from) < 1e-9 || Math.Abs(g.FromDepthM - from) < 1e-9)?.GapKind;
                if (inGap)
                {
                    segments.Add(new(i, from, to, gapHere?.GapKind ?? "gap", new(), false, "boundary"));
                    continue;
                }
                var segAnchors = anchors.Where(a => a.Age.DepthM >= from && a.Age.DepthM <= to).ToList();
                if (segAnchors.Count == 0)
                {
                    segments.Add(new(i, from, to, lowerBoundary, new(), false, "empty"));
                    continue;
                }

                // 倒置层段：随深度年龄变新超出容差
                var ordered = segAnchors.OrderBy(a => a.Age.DepthM).ThenBy(a => a.Age.Id, StringComparer.Ordinal).ToList();
                var inverted = false;
                for (var k = 1; k < ordered.Count; k++)
                    if (Median(ordered[k].CorrectedSamplesKa) < Median(ordered[k - 1].CorrectedSamplesKa) - p.MonotonicToleranceKa)
                    {
                        inverted = true;
                        diags.Add(new("inverted-section",
                            $"segment {i}: anchor {ordered[k].Age.Id} at {ordered[k].Age.DepthM} m is younger than shallower {ordered[k - 1].Age.Id}; flagged, monotonic fit refused for this pair",
                            ordered[k].Age.Id, ordered[k].Age.DepthM, "error"));
                    }

                // 并列锚点：同深度多组分布 → 枚举等优候选
                var byDepth = ordered.GroupBy(a => a.Age.DepthM).OrderBy(g => g.Key).ToList();
                var choiceSets = byDepth.Select(g => g.ToList()).ToList();
                var combos = EnumerateCombos(choiceSets, p.MaxCandidates, () => Spend(1));

                var segCandidates = new List<ModelCandidate>();
                foreach (var combo in combos)
                {
                    Spend(20);
                    var comboList = combo;
                    // 单调性只在不断层段内约束；倒置组合被剔除而非静默修正
                    var ok = true;
                    for (var k = 1; k < comboList.Count; k++)
                        if (Median(comboList[k].CorrectedSamplesKa) < Median(comboList[k - 1].CorrectedSamplesKa) - p.MonotonicToleranceKa)
                        { ok = false; break; }
                    if (!ok) continue;
                    var cand = BuildCandidate(coreId, i, segCandidates.Count, from, to, comboList, p, atSeq);
                    segCandidates.Add(cand);
                }

                if (segCandidates.Count == 0 && segAnchors.Count > 0)
                {
                    // 语义无解：与格式损坏、资源耗尽是不同结果
                    semanticallyBroken.Add($"segment {i}");
                    segments.Add(new(i, from, to, lowerBoundary, segAnchors.Select(a => a.Age.Id).ToList(), inverted, "unsolvable"));
                    diags.Add(new("semantic-unsolvable",
                        $"segment {i}: no monotonic candidate satisfies all anchor distributions within tolerance; anchors kept, no model forced",
                        null, from, "error"));
                    continue;
                }

                candidates.AddRange(segCandidates);
                segments.Add(new(i, from, to, lowerBoundary, segAnchors.Select(a => a.Age.Id).ToList(), inverted,
                    inverted ? "inverted" : "ok"));
            }

            var result = new DerivationResult(segments, candidates, diags);
            return semanticallyBroken.Count > 0 && candidates.Count == 0
                ? new Outcome<DerivationResult> { Status = OutcomeStatus.SemanticUnsolvable, Value = result, Diagnostics = diags }
                : new Outcome<DerivationResult> { Status = diags.Any(d => d.Severity == "error") ? OutcomeStatus.Diagnostics : OutcomeStatus.Ok, Value = result, Diagnostics = diags };
        }
        catch (BudgetExhausted)
        {
            return Outcome<DerivationResult>.Fail(OutcomeStatus.BudgetExhausted,
                new Diagnostic("budget-exhausted", "derivation resource budget exhausted; partial results discarded", null, null, "error"));
        }
    }

    private sealed class BudgetExhausted : Exception { }

    private static List<List<Anchor>> EnumerateCombos(List<List<Anchor>> sets, int max, Action spend)
    {
        var acc = new List<List<Anchor>> { new() };
        foreach (var set in sets)
        {
            var next = new List<List<Anchor>>();
            foreach (var prefix in acc)
                foreach (var a in set)
                {
                    spend();
                    if (next.Count >= max) break;
                    next.Add(prefix.Append(a).ToList());
                }
            acc = next;
            if (acc.Count >= max) break;
        }
        return acc;
    }

    private static ModelCandidate BuildCandidate(string coreId, int segIdx, int candIdx, double from, double to,
        List<Anchor> anchors, ParamSet p, long atSeq)
    {
        var pts = anchors.Select(a => new AnchorPoint(a.Age.Id, a.Age.DepthM,
            Median(a.CorrectedSamplesKa), Quantile(a.CorrectedSamplesKa, 0.05), Quantile(a.CorrectedSamplesKa, 0.95))).ToList();

        // 速率区间：相邻锚点深度差 / 年龄差（用分位数取区间，不用均值）
        var rateMin = double.PositiveInfinity; var rateMax = 0.0;
        for (var k = 1; k < pts.Count; k++)
        {
            var dDepthCm = (pts[k].DepthM - pts[k - 1].DepthM) * 100.0;
            var dAgeMin = Math.Max(pts[k].P05Ka - pts[k - 1].P95Ka, 1e-6);
            var dAgeMax = Math.Max(pts[k].P95Ka - pts[k - 1].P05Ka, 1e-6);
            rateMin = Math.Min(rateMin, dDepthCm / dAgeMax);
            rateMax = Math.Max(rateMax, dDepthCm / dAgeMin);
        }
        if (pts.Count == 1) { rateMin = 0; rateMax = 0; }

        // 不确定度带：锚点分位数随深度分段线性插值
        var band = new List<BandPoint>();
        const int steps = 24;
        for (var sIdx = 0; sIdx <= steps; sIdx++)
        {
            var d = from + (to - from) * sIdx / steps;
            var (lo, mid, hiq) = Interp(pts, d);
            band.Add(new(d, lo, mid, hiq));
        }

        var deps = anchors.Select(a => a.Age.Id)
            .Concat(anchors.Select(a => a.Age.ReservoirOffsetId).Where(x => x is not null)!)
            .Cast<string>().Distinct().OrderBy(x => x, StringComparer.Ordinal).ToList();
        var id = $"model:{coreId}:seg{segIdx}:cand{candIdx}";
        return new ModelCandidate
        {
            Id = id, CoreId = coreId, SegmentIndex = segIdx, FromDepthM = from, ToDepthM = to,
            CandidateIndex = candIdx, Anchors = pts, RateMinCmPerKa = rateMin, RateMaxCmPerKa = rateMax,
            UncertaintyBand = band, InputHash = Hashing.OfJson(pts), ParamVersion = p.Version,
            DependsOn = deps, DerivedAtSeq = atSeq
        };
    }

    private static (double lo, double mid, double hi) Interp(List<AnchorPoint> pts, double depth)
    {
        if (pts.Count == 1) return (pts[0].P05Ka, pts[0].MedianKa, pts[0].P95Ka);
        AnchorPoint a = pts[0], b = pts[^1];
        for (var k = 0; k < pts.Count - 1; k++)
            if (depth >= pts[k].DepthM && depth <= pts[k + 1].DepthM) { a = pts[k]; b = pts[k + 1]; break; }
        if (depth <= pts[0].DepthM) { a = pts[0]; b = pts[0]; }
        if (depth >= pts[^1].DepthM) { a = pts[^1]; b = pts[^1]; }
        var span = b.DepthM - a.DepthM;
        var t = span < 1e-9 ? 0 : (depth - a.DepthM) / span;
        double L(double x, double y) => x + (y - x) * t;
        return (L(a.P05Ka, b.P05Ka), L(a.MedianKa, b.MedianKa), L(a.P95Ka, b.P95Ka));
    }

    public static double Median(double[] sorted) => Quantile(sorted, 0.5);

    public static double Quantile(double[] sorted, double q)
    {
        if (sorted.Length == 0) return double.NaN;
        var pos = q * (sorted.Length - 1);
        var lo = (int)Math.Floor(pos); var hi = (int)Math.Ceiling(pos);
        return sorted[lo] + (sorted[hi] - sorted[lo]) * (pos - lo);
    }
}
