using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace AgeDepthLab.Core;

/// <summary>连续不断层段：由显式边界（缺口/不整合）切分出的深度区间。单调性只在段内成立。</summary>
public sealed record ContinuousRun(
    string Id, double TopM, double BottomM, string Datum,
    string? UpperBoundaryId, string? LowerBoundaryId);

public sealed record AnchorPoint(
    string AnchorId, double DepthM, double AgeYr, double DrawIndex);

public sealed record BandPoint(double DepthM, double P2_5, double P50, double P97_5);

public sealed record RunModel(
    string RunId,
    IReadOnlyList<BandPoint> Band,          // 不确定度带（分位数，来自集合而非均值）
    double RateMinMYr, double RateMaxMYr,   // 速率区间 m/kyr
    int ValidDraws, int Attempts,
    IReadOnlyList<string> ConstraintAnchorIds,
    Dictionary<string, double[]> SampledAgesByAnchor, // 每个锚点的抽样（分布不被均值替代的证据）
    string InputHash);

public sealed record CandidateModel(
    string Id,
    IReadOnlyDictionary<string, string> TieChoices, // 并列锚点组 -> 被采纳锚点
    IReadOnlyList<RunModel> Runs,
    int TotalValidDraws,
    IReadOnlyList<RunDiagnostic> Diagnostics);

public sealed record Derivation(
    string InputHash, int ParameterVersion,
    IReadOnlyList<ContinuousRun> Runs,
    IReadOnlyList<CandidateModel> Candidates,
    IReadOnlyList<RunDiagnostic> Diagnostics,
    IReadOnlyList<string> RecomputedRunIds);

public static class AgeModel
{
    public const int MaxCandidates = 16; // 候选分支预算

    /// <summary>把材料切成连续段：缺口与不整合是显式边界。</summary>
    public static RunResult<IReadOnlyList<ContinuousRun>> BuildRuns(MaterialSet ms, List<RunDiagnostic> diags)
    {
        var edges = new SortedSet<double>();
        string datum = ms.Datum.Convention;
        foreach (var s in ms.Segments)
        {
            var t = UnitConverter.Depth(s.Top, s.Id);
            var b = UnitConverter.Depth(s.Bottom, s.Id);
            if (!t.Ok) { diags.Add(t.Diagnostic!); continue; }
            if (!b.Ok) { diags.Add(b.Diagnostic!); continue; }
            edges.Add(t.Normalized!.Value.Value);
            edges.Add(b.Normalized!.Value.Value);
        }
        foreach (var bd in ms.Boundaries)
        {
            var at = UnitConverter.Depth(bd.At, bd.Id);
            if (!at.Ok) { diags.Add(at.Diagnostic!); continue; }
            edges.Add(at.Normalized!.Value.Value);
            diags.Add(new RunDiagnostic(
                bd.Kind == SegmentBoundary.Unconformity ? "BOUNDARY_UNCONFORMITY" : "BOUNDARY_GAP",
                "info", $"显式边界：{(bd.Kind == SegmentBoundary.Unconformity ? "不整合" : "深度缺口")}，单调性到此为止",
                bd.Id, at.Normalized!.Value.Value, bd.At.Datum));
        }
        if (edges.Count < 2)
            return RunResult<IReadOnlyList<ContinuousRun>>.Failure(ResultKind.SemanticUnsolvable,
                new RunDiagnostic("DEPTH_FRAME_EMPTY", "error", "没有足够的深度边界构成层段", null, null, datum));

        var list = edges.ToList();
        var runs = new List<ContinuousRun>();
        for (int i = 0; i < list.Count - 1; i++)
        {
            var top = list[i]; var bot = list[i + 1];
            var upper = ms.Boundaries.FirstOrDefault(b => SameDepth(b.At, top))?.Id;
            var lower = ms.Boundaries.FirstOrDefault(b => SameDepth(b.At, bot))?.Id;
            runs.Add(new ContinuousRun($"run-{i + 1:D2}", top, bot, datum, upper, lower));
        }
        return RunResult<IReadOnlyList<ContinuousRun>>.Success(runs);
    }

    private static bool SameDepth(Quantity q, double m)
    {
        var c = UnitConverter.Depth(q, "boundary");
        return c.Ok && Math.Abs(c.Normalized!.Value.Value - m) < 1e-9;
    }

    private sealed record NA(string AnchorId, double DepthM, AgeDistribution Dist, double OffsetYr, string Raw);

    private static RunResult<List<NA>> NormalizeAnchors(MaterialSet ms, IReadOnlySet<string> excluded, List<RunDiagnostic> diags)
    {
        var list = new List<NA>();
        foreach (var a in ms.Anchors.OrderBy(a => a.Id, StringComparer.Ordinal))
        {
            if (excluded.Contains(a.Id)) continue;
            var d = UnitConverter.Depth(a.Depth, a.Id);
            if (!d.Ok) { diags.Add(d.Diagnostic!); continue; } // 无法转换的值留在原位置
            double offset = 0;
            if (a.ReservoirOffset != null)
            {
                var o = UnitConverter.OffsetYears(a.ReservoirOffset, a.Id);
                if (!o.Ok) { diags.Add(o.Diagnostic!); continue; }
                offset = o.Normalized!.Value.Value;
            }
            list.Add(new NA(a.Id, d.Normalized!.Value.Value, a.Distribution, offset, a.Depth.Raw));
        }
        // 深度重复检测（含并列锚点）
        foreach (var g in list.GroupBy(x => x.DepthM).Where(g => g.Count() > 1))
            diags.Add(new RunDiagnostic("DEPTH_DUPLICATE", "info",
                $"深度 {g.Key} m 处存在 {g.Count()} 个并列锚点，全部保留并分支候选",
                string.Join(",", g.Select(x => x.AnchorId)), g.Key, ms.Datum.Convention));
        return RunResult<List<NA>>.Success(list);
    }

    /// <summary>完整推导。等价输入得到稳定顺序与稳定身份。</summary>
    public static RunResult<Derivation> Derive(
        MaterialSet ms, ModelParameters p, IReadOnlySet<string> excluded,
        Func<string, string, RunModel?>? runCache = null)
    {
        var diags = new List<RunDiagnostic>();
        var runsR = BuildRuns(ms, diags);
        if (!runsR.IsOk) return RunResult<Derivation>.Failure(runsR.Kind, runsR.Diagnostics.ToArray());
        var anchorsR = NormalizeAnchors(ms, excluded, diags);
        var anchors = anchorsR.Value!;
        var inputHash = ms.ContentHash() + "|excl:" + string.Join(",", excluded.OrderBy(x => x)) + "|pv:" + p.Version;

        // 并列锚点分组 -> 候选分支（笛卡尔选择，受预算限制）
        var tieGroups = anchors.GroupBy(a => a.DepthM).Where(g => g.Count() > 1)
            .OrderBy(g => g.Key).ToList();
        var combos = new List<Dictionary<string, string>> { new() };
        foreach (var g in tieGroups)
        {
            var next = new List<Dictionary<string, string>>();
            foreach (var combo in combos)
                foreach (var a in g.OrderBy(x => x.AnchorId, StringComparer.Ordinal))
                {
                    var c = new Dictionary<string, string>(combo) { [TieKey(g.Key)] = a.AnchorId };
                    next.Add(c);
                }
            combos = next;
            if (combos.Count > MaxCandidates)
                return RunResult<Derivation>.Failure(ResultKind.BudgetExhausted,
                    new RunDiagnostic("CANDIDATE_BUDGET", "error",
                        $"并列锚点分支超过候选预算 {MaxCandidates}", null, g.Key, ms.Datum.Convention));
        }

        var recomputed = new List<string>();
        var candidates = new List<CandidateModel>();
        foreach (var combo in combos)
        {
            var chosen = new HashSet<string>(combo.Values);
            var active = anchors.Where(a =>
                !tieGroups.Any(g => Math.Abs(g.Key - a.DepthM) < 1e-9) || chosen.Contains(a.AnchorId)).ToList();
            var candRuns = new List<RunModel>();
            var candDiags = new List<RunDiagnostic>();
            int totalValid = 0;
            foreach (var run in runsR.Value!)
            {
                var inRun = active.Where(a => a.DepthM >= run.TopM - 1e-9 && a.DepthM <= run.BottomM + 1e-9)
                    .OrderBy(a => a.DepthM).ThenBy(a => a.AnchorId, StringComparer.Ordinal).ToList();
                var runInputHash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(
                    JsonSerializer.Serialize(new
                    {
                        run.TopM, run.BottomM,
                        A = inRun.Select(a => new { a.AnchorId, a.DepthM, a.OffsetYr, a.Dist }).ToList(),
                        p.DrawCount, p.Seed, p.MonotonicToleranceYr
                    }, JsonOptions.Default))))[..16];

                var cached = runCache?.Invoke(run.Id, runInputHash);
                RunResult<RunModel> rm;
                if (cached != null)
                {
                    rm = RunResult<RunModel>.Success(cached);
                }
                else
                {
                    recomputed.Add(run.Id);
                    rm = FitRun(run, inRun, p, runInputHash);
                }
                candDiags.AddRange(rm.Diagnostics);
                if (!rm.IsOk)
                {
                    if (rm.Kind == ResultKind.BudgetExhausted)
                        return RunResult<Derivation>.Failure(ResultKind.BudgetExhausted,
                            rm.Diagnostics.Concat(diags).ToArray());
                    // 语义无解的段：保留诊断，候选其它段仍保留（影响被限制在正确范围）
                    candDiags.Add(new RunDiagnostic("RUN_UNRESOLVED", "error",
                        $"层段 {run.Id} 无解，已从候选中剔除但保留证据", run.Id, run.TopM, run.Datum));
                    continue;
                }
                totalValid += rm.Value!.ValidDraws;
                candRuns.Add(rm.Value);
            }
            var cid = CandidateId(inputHash, combo);
            candidates.Add(new CandidateModel(cid, combo, candRuns, totalValid, candDiags));
        }

        // 稳定顺序：按候选身份排序；等优模型全部保留
        candidates = candidates.OrderBy(c => c.Id, StringComparer.Ordinal).ToList();
        return RunResult<Derivation>.Success(
            new Derivation(inputHash, p.Version, runsR.Value!, candidates, diags, recomputed), diags);
    }

    public static string TieKey(double depthM) => "tie@" + depthM.ToString("G17");

    public static string CandidateId(string inputHash, IReadOnlyDictionary<string, string> combo)
    {
        var s = inputHash + "|" + string.Join(",", combo.OrderBy(kv => kv.Key).Select(kv => kv.Key + "=" + kv.Value));
        return "cand-" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(s)))[..12];
    }

    /// <summary>单个连续段的集合拟合。年龄只能在不断层段中单调。</summary>
    private static RunResult<RunModel> FitRun(ContinuousRun run, List<NA> anchors, ModelParameters p, string runInputHash)
    {
        var diags = new List<RunDiagnostic>();
        if (anchors.Count == 0)
            return RunResult<RunModel>.Failure(ResultKind.SemanticUnsolvable,
                new RunDiagnostic("RUN_WITHOUT_ANCHOR", "warning",
                    $"层段 {run.Id} 内没有可用锚点，无法建立年龄模型", run.Id, run.TopM, run.Datum));

        var rng = new Random(unchecked(p.Seed * 31 + run.Id.GetHashCode()));
        var valid = new List<double[]>();
        var sampled = anchors.ToDictionary(a => a.AnchorId, _ => new List<double>());
        int attempts = 0, inversions = 0;
        while (valid.Count < p.DrawCount)
        {
            attempts++;
            if (attempts > p.MaxDrawAttempts)
                return RunResult<RunModel>.Failure(ResultKind.BudgetExhausted,
                    new RunDiagnostic("DRAW_BUDGET", "error",
                        $"层段 {run.Id} 抽取次数超过预算 {p.MaxDrawAttempts}", run.Id, run.TopM, run.Datum));
            var pts = new double[anchors.Count][];
            bool bad = false;
            for (int i = 0; i < anchors.Count; i++)
            {
                var age = anchors[i].Dist.Draw(rng, anchors[i].AnchorId, diags);
                if (double.IsNaN(age)) { bad = true; break; }
                age -= anchors[i].OffsetYr; // 储层偏移：从表观年龄中扣除
                pts[i] = new[] { anchors[i].DepthM, age };
                sampled[anchors[i].AnchorId].Add(age);
            }
            if (bad) continue;
            // 段内单调性检查（深度递增 => 年龄不减，允许容差）
            bool mono = true;
            for (int i = 1; i < pts.Length; i++)
                if (pts[i][1] < pts[i - 1][1] - p.MonotonicToleranceYr) { mono = false; break; }
            if (!mono) { inversions++; continue; }
            valid.Add(pts.SelectMany(x => x).ToArray());
        }
        if (valid.Count < p.MinValidDraws)
            return RunResult<RunModel>.Failure(ResultKind.SemanticUnsolvable,
                new RunDiagnostic("SEGMENT_INVERSION", "error",
                    $"层段 {run.Id} 内年龄随深度倒置：{attempts} 次尝试中仅 {valid.Count} 次满足单调，约束下无解",
                    run.Id, run.TopM, run.Datum));

        // 每个有效抽样：折线延伸到段边界（边缘用最近邻速率外推）
        var gridN = 25;
        var grid = new double[gridN];
        for (int i = 0; i < gridN; i++) grid[i] = run.TopM + (run.BottomM - run.TopM) * i / (gridN - 1);
        var ageAt = valid.Select(draw => grid.Select(d => Interp(draw, d)).ToArray()).ToList();

        var band = new List<BandPoint>();
        for (int i = 0; i < gridN; i++)
        {
            var col = ageAt.Select(a => a[i]).OrderBy(x => x).ToArray();
            band.Add(new BandPoint(grid[i], Pct(col, 2.5), Pct(col, 50), Pct(col, 97.5)));
        }
        var rates = valid.Select(draw =>
        {
            double spanD = draw[^2] == draw[0] ? 1e-9 : draw[^2] - draw[0];
            return Math.Abs((draw[^1] - draw[1]) / spanD) / 1000.0; // yr/m -> kyr/m
        }).OrderBy(x => x).ToArray();
        double rLo = Pct(rates, 2.5), rHi = Pct(rates, 97.5);
        // 速率区间以 m/kyr 表达
        double ToMYr(double kyrPerM) => kyrPerM <= 1e-9 ? 1e9 : 1.0 / kyrPerM;

        if (inversions > 0)
            diags.Add(new RunDiagnostic("SEGMENT_INVERSION", "warning",
                $"层段 {run.Id} 有 {inversions}/{attempts} 次抽样因倒置被拒绝，仅保留合法集合",
                run.Id, run.TopM, run.Datum));

        return RunResult<RunModel>.Success(new RunModel(
            run.Id, band, ToMYr(rHi), ToMYr(rLo), valid.Count, attempts,
            anchors.Select(a => a.AnchorId).ToList(),
            sampled.ToDictionary(kv => kv.Key, kv => kv.Value.ToArray()),
            runInputHash), diags);
    }

    private static double Interp(double[] draw, double depth)
    {
        // draw: [d0,a0,d1,a1,...]
        int n = draw.Length / 2;
        if (depth <= draw[0])
        {
            if (n == 1) return draw[1];
            var rate = (draw[3] - draw[1]) / Math.Max(1e-9, draw[2] - draw[0]);
            return draw[1] + (depth - draw[0]) * rate;
        }
        if (depth >= draw[^2])
        {
            if (n == 1) return draw[1];
            var rate = (draw[^1] - draw[^3]) / Math.Max(1e-9, draw[^2] - draw[^4]);
            return draw[^1] + (depth - draw[^2]) * rate;
        }
        for (int i = 0; i < n - 1; i++)
        {
            double d0 = draw[2 * i], a0 = draw[2 * i + 1], d1 = draw[2 * i + 2], a1 = draw[2 * i + 3];
            if (depth >= d0 && depth <= d1)
            {
                var t = d1 - d0 < 1e-9 ? 0 : (depth - d0) / (d1 - d0);
                return a0 + t * (a1 - a0);
            }
        }
        return draw[^1];
    }

    private static double Pct(IReadOnlyList<double> sorted, double p)
    {
        if (sorted.Count == 0) return double.NaN;
        var idx = p / 100.0 * (sorted.Count - 1);
        int lo = (int)Math.Floor(idx), hi = (int)Math.Ceiling(idx);
        if (lo == hi) return sorted[lo];
        return sorted[lo] + (sorted[hi] - sorted[lo]) * (idx - lo);
    }
}
