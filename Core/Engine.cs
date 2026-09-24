using System.Globalization;
using System.Text;

namespace DepthAge.Core;

public sealed record EngineParams(
    int Samples = 240,
    int MaxCandidates = 6,
    double EquivalenceLogTolerance = 2.0,
    long Budget = 200_000,
    string ParamVersion = "params/v1",
    double DefaultReservoirOffsetKa = 0.40);

public sealed record RateInterval(double MinMPerKa, double MaxMPerKa, bool Unbounded)
{
    public override string ToString() => Unbounded
        ? $">{MinMPerKa.ToString("0.###", CultureInfo.InvariantCulture)} {UnitRegistry.CanonicalRateUnit} (unbounded)"
        : $"{MinMPerKa.ToString("0.###", CultureInfo.InvariantCulture)}\u2013{MaxMPerKa.ToString("0.###", CultureInfo.InvariantCulture)} {UnitRegistry.CanonicalRateUnit}";
}

public sealed record CandidateModel(string Id, double Score, double[] DepthsM, double[] AgesKa);

public sealed record SegmentFit(
    string Segment,
    FitStatus Status,
    double TopM,
    double BottomM,
    string TopBoundary,
    string BottomBoundary,
    IReadOnlyList<CandidateModel> Candidates,
    double[] BandDepthsM,
    double[] P05,
    double[] P50,
    double[] P95,
    IReadOnlyList<RateInterval> Rates,
    IReadOnlyList<Diagnostic> Diagnostics,
    IReadOnlyList<string> AnchorRefs);

public sealed record ModelSet(
    string ParamVersion,
    string InputDigest,
    IReadOnlyList<SegmentFit> Segments,
    IReadOnlyList<Diagnostic> Diagnostics);

public sealed record FitOutcome(FitStatus Status, ModelSet? Model, IReadOnlyList<Diagnostic> Diagnostics)
{
    public static FitOutcome Exhausted(IReadOnlyList<Diagnostic> diagnostics) =>
        new(FitStatus.BudgetExhausted, null, diagnostics);

    public static FitOutcome Failed(ModelSet model, IReadOnlyList<Diagnostic> diagnostics) =>
        new(FitStatus.Unsatisfiable, model, diagnostics);

    public static FitOutcome Ok(ModelSet model, IReadOnlyList<Diagnostic> diagnostics) =>
        new(FitStatus.Ok, model, diagnostics);
}

/// <summary>引擎输入段（保留原始 Quantity，规范化在引擎内进行）。</summary>
public sealed record SegmentInput(string Ref, Quantity Top, Quantity Bottom, string TopBoundary, string BottomBoundary);

public sealed record AnchorInput(
    string Ref,
    string MaterialId,
    string SourceKind,
    Quantity Depth,
    AgeDistribution Age,
    Quantity? ReservoirOffset);

internal sealed record NormAnchor(string Ref, string MaterialId, string SourceKind, double DepthM, AgeDistribution DistKa);

/// <summary>
/// 深度-年龄模型引擎（纯函数、可直接被测试调用）。
/// 规则核心：
///  1) 年龄只能在“不断层段”内单调；gap/unconformity 是显式边界，跨边界允许年龄跳跃。
///  2) 锚点以完整分布参与抽样（长尾保留样本点），绝不退化为均值。
///  3) 等优候选（对数评分在容差内且曲线不同）全部保留，顺序由内容决定，与输入顺序无关。
///  4) 预算耗尽、语义无解是两个不同结果；单位无法转换时原值留在材料层并附位置诊断。
/// </summary>
public static class Engine
{
    private const double DepthEps = 1e-6;

    public static FitOutcome Fit(
        IReadOnlyList<SegmentInput> segmentsIn,
        IReadOnlyList<AnchorInput> anchorsIn,
        EngineParams p)
    {
        var globalDiags = new List<Diagnostic>();
        long budget = p.Budget;
        bool Consume(long n)
        {
            budget -= n;
            return budget >= 0;
        }

        // ---- 派生层规范化（材料层的原始表示不受影响） ----
        var anchors = new List<NormAnchor>();
        foreach (var a in anchorsIn.OrderBy(a => a.Ref, StringComparer.Ordinal))
        {
            if (!UnitRegistry.TryConvert(a.Depth, QuantityKind.Depth, out var depthM, out var depthErr))
            {
                globalDiags.Add(new Diagnostic("unit-unconvertible", Diagnostic.Error,
                    $"anchor {a.Ref}: depth {a.Depth} kept in place, excluded from fit: {depthErr}",
                    a.Ref, $"anchor:{a.Ref}/depth"));
                continue;
            }

            var ageFactor = AgeUnitFactor(a.Age.Unit, out var ageErr);
            if (ageFactor is null)
            {
                globalDiags.Add(new Diagnostic("unit-unconvertible", Diagnostic.Error,
                    $"anchor {a.Ref}: age unit '{a.Age.Unit}' kept in place, excluded from fit: {ageErr}",
                    a.Ref, $"anchor:{a.Ref}/age"));
                continue;
            }

            double offsetKa;
            if (a.ReservoirOffset is { } off)
            {
                if (UnitRegistry.TryConvert(off, QuantityKind.Offset, out var oc, out var offErr))
                    offsetKa = oc.Value;
                else
                {
                    globalDiags.Add(new Diagnostic("unit-unconvertible", Diagnostic.Warning,
                        $"anchor {a.Ref}: reservoir offset {off} not applied ({offErr}); zero offset used",
                        a.Ref, $"anchor:{a.Ref}/reservoirOffset"));
                    offsetKa = 0;
                }
            }
            else
            {
                offsetKa = p.DefaultReservoirOffsetKa;
            }

            // 测年物质偏老 → 沉积年龄 = 测量年龄 - 储层偏移（整条分布平移）。
            var distKa = a.Age.WithLinearTransform(ageFactor.Value, -offsetKa, UnitRegistry.CanonicalAgeUnit);
            anchors.Add(new NormAnchor(a.Ref, a.MaterialId, a.SourceKind, depthM.Value, distKa));
        }

        var segments = new List<SegmentFit>();
        var orderedSegments = segmentsIn.OrderBy(s => s.Ref, StringComparer.Ordinal).ToList();

        foreach (var s in orderedSegments)
        {
            var sdiags = new List<Diagnostic>();
            if (!UnitRegistry.TryConvert(s.Top, QuantityKind.Depth, out var topM, out var topErr))
            {
                sdiags.Add(new Diagnostic("unit-unconvertible", Diagnostic.Error,
                    $"segment {s.Ref}: top {s.Top} kept in place: {topErr}", s.Ref, $"segment:{s.Ref}/top"));
                segments.Add(FailedSegment(s, 0, 0, sdiags));
                continue;
            }
            if (!UnitRegistry.TryConvert(s.Bottom, QuantityKind.Depth, out var bottomM, out var botErr))
            {
                sdiags.Add(new Diagnostic("unit-unconvertible", Diagnostic.Error,
                    $"segment {s.Ref}: bottom {s.Bottom} kept in place: {botErr}", s.Ref, $"segment:{s.Ref}/bottom"));
                segments.Add(FailedSegment(s, topM.Value, 0, sdiags));
                continue;
            }
            if (topM.Value >= bottomM.Value)
            {
                sdiags.Add(new Diagnostic("segment-bounds-inverted", Diagnostic.Error,
                    $"segment {s.Ref}: top depth {topM.Value:R} m must be above bottom depth {bottomM.Value:R} m",
                    s.Ref, $"segment:{s.Ref}"));
                segments.Add(FailedSegment(s, topM.Value, bottomM.Value, sdiags));
                continue;
            }

            if (string.Equals(s.TopBoundary, "unconformity", StringComparison.OrdinalIgnoreCase))
                sdiags.Add(new Diagnostic("unconformity-boundary", Diagnostic.Info,
                    $"segment {s.Ref}: explicit unconformity boundary at {topM.Value:R} m; ages may jump across it",
                    s.Ref, $"segment:{s.Ref}/topBoundary"));
            if (string.Equals(s.BottomBoundary, "unconformity", StringComparison.OrdinalIgnoreCase))
                sdiags.Add(new Diagnostic("unconformity-boundary", Diagnostic.Info,
                    $"segment {s.Ref}: explicit unconformity boundary at {bottomM.Value:R} m; ages may jump across it",
                    s.Ref, $"segment:{s.Ref}/bottomBoundary"));
            if (string.Equals(s.TopBoundary, "gap", StringComparison.OrdinalIgnoreCase))
                sdiags.Add(new Diagnostic("gap-boundary", Diagnostic.Info,
                    $"segment {s.Ref}: explicit core gap above {topM.Value:R} m; monotonicity is not enforced across it",
                    s.Ref, $"segment:{s.Ref}/topBoundary"));
            if (string.Equals(s.BottomBoundary, "gap", StringComparison.OrdinalIgnoreCase))
                sdiags.Add(new Diagnostic("gap-boundary", Diagnostic.Info,
                    $"segment {s.Ref}: explicit core gap below {bottomM.Value:R} m; monotonicity is not enforced across it",
                    s.Ref, $"segment:{s.Ref}/bottomBoundary"));

            var sa = anchors
                .Where(a => a.DepthM + DepthEps >= topM.Value && a.DepthM - DepthEps <= bottomM.Value)
                .OrderBy(a => a.DepthM)
                .ThenBy(a => a.Ref, StringComparer.Ordinal)
                .ToList();

            if (sa.Count < 2)
            {
                sdiags.Add(new Diagnostic("insufficient-anchors", Diagnostic.Info,
                    $"segment {s.Ref}: only {sa.Count} usable anchor(s); a monotonic curve cannot be constructed",
                    s.Ref, $"segment:{s.Ref}"));
                segments.Add(new SegmentFit(s.Ref, FitStatus.Ok, topM.Value, bottomM.Value,
                    s.TopBoundary, s.BottomBoundary, Array.Empty<CandidateModel>(),
                    Array.Empty<double>(), Array.Empty<double>(), Array.Empty<double>(), Array.Empty<double>(),
                    Array.Empty<RateInterval>(), sdiags, sa.Select(a => a.Ref).ToArray()));
                continue;
            }

            // ---- 深度重复（同深同分布）与并列锚点（同深不同分布） ----
            var hardConflict = false;
            for (var i = 0; i < sa.Count; i++)
            {
                var group = sa.Where(a => Math.Abs(a.DepthM - sa[i].DepthM) <= DepthEps).ToList();
                if (group.Count <= 1 || group[0].Ref != sa[i].Ref) continue;
                var distinctKeys = group.Select(g => DistributionKey(g.DistKa)).Distinct().ToList();
                if (distinctKeys.Count < group.Count)
                {
                    var dupRefs = group.GroupBy(g => DistributionKey(g.DistKa))
                        .Where(grp => grp.Count() > 1).SelectMany(grp => grp).Select(g => g.Ref).ToList();
                    sdiags.Add(new Diagnostic("depth-duplicate", Diagnostic.Warning,
                        $"segment {s.Ref}: anchors {string.Join(", ", dupRefs)} sit at the same depth " +
                        $"{sa[i].DepthM.ToString("R", CultureInfo.InvariantCulture)} m with identical age distributions (possible duplicate entry); all retained",
                        string.Join(",", dupRefs), $"segment:{s.Ref}/depth:{sa[i].DepthM.ToString("R", CultureInfo.InvariantCulture)}"));
                }
                if (distinctKeys.Count > 1)
                    sdiags.Add(new Diagnostic("parallel-anchors", Diagnostic.Info,
                        $"segment {s.Ref}: {group.Count} co-located anchors at depth {sa[i].DepthM.ToString("R", CultureInfo.InvariantCulture)} m " +
                        $"with {distinctKeys.Count} distinct age distributions: {string.Join(", ", group.Select(g => g.Ref))}; all retained as alternatives",
                        string.Join(",", group.Select(g => g.Ref)), $"segment:{s.Ref}/depth:{sa[i].DepthM.ToString("R", CultureInfo.InvariantCulture)}"));
                var lo = group.Max(g => g.DistKa.Quantile(0.05));
                var hi = group.Min(g => g.DistKa.Quantile(0.95));
                if (lo > hi)
                {
                    sdiags.Add(new Diagnostic("parallel-conflict", Diagnostic.Error,
                        $"segment {s.Ref}: co-located anchors at {sa[i].DepthM:R} m have disjoint 90% intervals; no shared age exists",
                        string.Join(",", group.Select(g => g.Ref)), $"segment:{s.Ref}/depth:{sa[i].DepthM:R}"));
                    hardConflict = true;
                }
            }

            // ---- 长尾：保留样本分布，绝不替换为均值 ----
            foreach (var a in sa)
            {
                if (a.DistKa.IsLongTail)
                    sdiags.Add(new Diagnostic("long-tail", Diagnostic.Info,
                        $"anchor {a.Ref}: long-tailed empirical distribution (skew={a.DistKa.Skewness()!.Value:0.00}) retained as samples; the mean is never substituted",
                        a.Ref, $"anchor:{a.Ref}/age"));
            }

            // ---- 倒置层段：段内年龄必须非递减（用 5%/95% 分位数判定硬性倒置） ----
            var inverted = false;
            for (var i = 0; i < sa.Count; i++)
            for (var j = i + 1; j < sa.Count; j++)
            {
                if (sa[i].DepthM >= sa[j].DepthM - DepthEps) continue;
                if (sa[i].DistKa.Quantile(0.05) <= sa[j].DistKa.Quantile(0.95)) continue;
                sdiags.Add(new Diagnostic("inverted-segment", Diagnostic.Error,
                    $"segment {s.Ref}: age inversion between shallower {sa[i].Ref} (q05={sa[i].DistKa.Quantile(0.05):0.###} ka) " +
                    $"and deeper {sa[j].Ref} (q95={sa[j].DistKa.Quantile(0.95):0.###} ka); monotonicity cannot be satisfied inside this unbroken segment",
                    $"{sa[i].Ref},{sa[j].Ref}", $"segment:{s.Ref}/pair:{sa[i].Ref}-{sa[j].Ref}"));
                inverted = true;
            }

            if (hardConflict || inverted)
            {
                segments.Add(new SegmentFit(s.Ref, FitStatus.Unsatisfiable, topM.Value, bottomM.Value,
                    s.TopBoundary, s.BottomBoundary, Array.Empty<CandidateModel>(),
                    Array.Empty<double>(), Array.Empty<double>(), Array.Empty<double>(), Array.Empty<double>(),
                    Array.Empty<RateInterval>(), sdiags, sa.Select(a => a.Ref).ToArray()));
                continue;
            }

            // ---- 确定性抽样 + PAVA 单调化 ----
            var seedText = string.Join("|", new[] { p.ParamVersion, s.Ref }.Concat(sa.Select(a => $"{a.Ref}@{a.DepthM:R}={DistributionKey(a.DistKa)}")));
            var rng = new XorShift(XorShift.SeedFrom(seedText));
            var curves = new List<(double Score, double[] Ages)>();
            var exhausted = false;
            for (var k = 0; k < p.Samples; k++)
            {
                if (!Consume(sa.Count + 1L)) { exhausted = true; break; }
                var drawn = sa.Select(a => a.DistKa.Draw(rng)).ToArray();
                PoolAdjacentViolators(drawn);
                double score = 0;
                for (var i = 0; i < sa.Count; i++) score += sa[i].DistKa.LogPdf(drawn[i]);
                curves.Add((score, drawn));
            }
            if (exhausted)
            {
                globalDiags.Add(new Diagnostic("budget-exhausted", Diagnostic.Error,
                    $"resource budget {p.Budget} exhausted while sampling segment {s.Ref}",
                    s.Ref, $"segment:{s.Ref}"));
                return FitOutcome.Exhausted(globalDiags.Concat(segments.SelectMany(x => x.Diagnostics)).ToList());
            }

            // ---- 等优候选：评分在容差内且曲线不同者全部保留；稳定排序（评分降序、身份升序） ----
            var ranked = curves
                .OrderByDescending(c => c.Score)
                .ThenBy(c => CurveKey(c.Ages), StringComparer.Ordinal)
                .ToList();
            var best = ranked[0].Score;
            var chosen = new List<(double Score, double[] Ages)>();
            foreach (var c in ranked)
            {
                if (chosen.Count >= p.MaxCandidates) break;
                if (c.Score < best - p.EquivalenceLogTolerance) break;
                if (chosen.Any(x => MaxAbsDiff(x.Ages, c.Ages) < 1e-6)) continue;
                chosen.Add(c);
            }
            var candidates = chosen
                .Select(c => new CandidateModel(
                    "cand-" + MaterialHashes.Sha256Hex(s.Ref + "|" + CurveKey(c.Ages))[..10],
                    c.Score,
                    sa.Select(a => a.DepthM).ToArray(),
                    c.Ages))
                .ToList();

            // ---- 不确定度带（同深度合并后取 5/50/95 分位） ----
            var uniqueDepths = sa.Select(a => a.DepthM).Distinct().OrderBy(d => d).ToArray();
            var p05 = new double[uniqueDepths.Length];
            var p50 = new double[uniqueDepths.Length];
            var p95 = new double[uniqueDepths.Length];
            for (var u = 0; u < uniqueDepths.Length; u++)
            {
                var idx = Enumerable.Range(0, sa.Count)
                    .Where(i => Math.Abs(sa[i].DepthM - uniqueDepths[u]) <= DepthEps).ToArray();
                var vals = curves.SelectMany(c => idx.Select(i => c.Ages[i])).OrderBy(v => v).ToArray();
                p05[u] = Percentile(vals, 0.05);
                p50[u] = Percentile(vals, 0.50);
                p95[u] = Percentile(vals, 0.95);
            }

            // ---- 速率区间（m/ka），年龄差为 0 时记为无界 ----
            var rates = new List<RateInterval>();
            for (var u = 0; u + 1 < uniqueDepths.Length; u++)
            {
                var dDepth = uniqueDepths[u + 1] - uniqueDepths[u];
                var idx0 = Enumerable.Range(0, sa.Count).Where(i => Math.Abs(sa[i].DepthM - uniqueDepths[u]) <= DepthEps).ToArray();
                var idx1 = Enumerable.Range(0, sa.Count).Where(i => Math.Abs(sa[i].DepthM - uniqueDepths[u + 1]) <= DepthEps).ToArray();
                double min = double.PositiveInfinity, max = double.NegativeInfinity;
                var unbounded = false;
                foreach (var c in curves)
                {
                    var a0 = idx0.Average(i => c.Ages[i]);
                    var a1 = idx1.Average(i => c.Ages[i]);
                    var dAge = a1 - a0;
                    if (dAge <= 1e-9) { unbounded = true; continue; }
                    var r = dDepth / dAge;
                    if (r < min) min = r;
                    if (r > max) max = r;
                }
                if (double.IsInfinity(min)) { min = 0; max = 0; }
                rates.Add(new RateInterval(min, max, unbounded));
            }

            segments.Add(new SegmentFit(s.Ref, FitStatus.Ok, topM.Value, bottomM.Value,
                s.TopBoundary, s.BottomBoundary, candidates,
                uniqueDepths, p05, p50, p95, rates, sdiags,
                sa.Select(a => a.Ref).ToArray()));
        }

        var model = new ModelSet(p.ParamVersion, DigestInput(segmentsIn, anchorsIn, p), segments, globalDiags);
        var allDiags = globalDiags.Concat(segments.SelectMany(x => x.Diagnostics)).ToList();
        var anyOk = segments.Any(x => x.Status == FitStatus.Ok);
        return anyOk ? FitOutcome.Ok(model, allDiags) : FitOutcome.Failed(model, allDiags);
    }

    private static SegmentFit FailedSegment(SegmentInput s, double top, double bottom, List<Diagnostic> diags) =>
        new(s.Ref, FitStatus.Unsatisfiable, top, bottom, s.TopBoundary, s.BottomBoundary,
            Array.Empty<CandidateModel>(), Array.Empty<double>(), Array.Empty<double>(),
            Array.Empty<double>(), Array.Empty<double>(), Array.Empty<RateInterval>(), diags,
            Array.Empty<string>());

    private static double? AgeUnitFactor(string unit, out string? error)
    {
        error = null;
        switch ((unit ?? string.Empty).Trim().ToLowerInvariant())
        {
            case "ka": case "kyr": return 1.0;
            case "yr": case "a": case "year": case "years": return 0.001;
            case "ma": case "myr": return 1000.0;
            default:
                error = $"age unit '{unit}' is not convertible to 'ka'";
                return null;
        }
    }

    private static string DistributionKey(AgeDistribution d) =>
        $"{d.Kind}|{d.Unit}|{d.Mean:R}|{d.Sigma:R}|{d.Min:R}|{d.Max:R}|" +
        (d.Samples is null ? "-" : string.Join(",", d.Samples.Select(v => v.ToString("R", CultureInfo.InvariantCulture))));

    private static string CurveKey(double[] ages) =>
        string.Join(",", ages.Select(a => a.ToString("0.0000", CultureInfo.InvariantCulture)));

    private static double MaxAbsDiff(double[] a, double[] b)
    {
        double m = 0;
        for (var i = 0; i < a.Length; i++) m = Math.Max(m, Math.Abs(a[i] - b[i]));
        return m;
    }

    private static double Percentile(double[] sorted, double p)
    {
        if (sorted.Length == 0) return 0;
        if (sorted.Length == 1) return sorted[0];
        var pos = p * (sorted.Length - 1);
        var i = (int)Math.Floor(pos);
        var frac = pos - i;
        return i >= sorted.Length - 1 ? sorted[^1] : sorted[i] * (1 - frac) + sorted[i + 1] * frac;
    }

    /// <summary>Pool Adjacent Violators：把抽样年龄单调化（非递减），等权合并违例相邻块。</summary>
    private static void PoolAdjacentViolators(double[] y)
    {
        var sums = new List<double>();
        var counts = new List<int>();
        foreach (var v in y)
        {
            sums.Add(v);
            counts.Add(1);
            while (sums.Count >= 2)
            {
                var n = sums.Count;
                var meanB = sums[n - 1] / counts[n - 1];
                var meanA = sums[n - 2] / counts[n - 2];
                if (meanA <= meanB) break;
                sums[n - 2] += sums[n - 1];
                counts[n - 2] += counts[n - 1];
                sums.RemoveAt(n - 1);
                counts.RemoveAt(n - 1);
            }
        }
        var k = 0;
        for (var b = 0; b < sums.Count; b++)
        {
            var mean = sums[b] / counts[b];
            for (var i = 0; i < counts[b]; i++) y[k++] = mean;
        }
    }

    private static string DigestInput(IReadOnlyList<SegmentInput> segments, IReadOnlyList<AnchorInput> anchors, EngineParams p)
    {
        var sb = new StringBuilder();
        sb.Append(p.ParamVersion).Append('|').Append(p.DefaultReservoirOffsetKa.ToString("R", CultureInfo.InvariantCulture));
        foreach (var s in segments.OrderBy(x => x.Ref, StringComparer.Ordinal))
            sb.Append("|seg:").Append(s.Ref).Append(':').Append(s.Top).Append(':').Append(s.Bottom)
              .Append(':').Append(s.TopBoundary).Append(':').Append(s.BottomBoundary);
        foreach (var a in anchors.OrderBy(x => x.Ref, StringComparer.Ordinal))
            sb.Append("|anc:").Append(a.Ref).Append(':').Append(a.Depth).Append(':')
              .Append(DistributionKey(a.Age)).Append(':')
              .Append(a.ReservoirOffset?.ToString() ?? "default");
        return "sha256:" + MaterialHashes.Sha256Hex(sb.ToString())[..16];
    }
}
