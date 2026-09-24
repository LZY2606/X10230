using System.Security.Cryptography;
using System.Text;

namespace AgeDepth;

/// <summary>Three deliberately distinct failure outcomes.</summary>
public enum FailureKind { BudgetExhausted, FormatCorrupt, SemanticUnsolvable }

public sealed record RateInterval(
    string FromAnchor, string ToAnchor, double TopDepthM, double BottomDepthM,
    double CentralKaPerM, double MinKaPerM, double MaxKaPerM);

public sealed record CandidateModel(
    string Id, string Branch, string CoreId, string RunId,
    string[] AnchorIds, double[] DepthsM, double[] MedianKa, double[] P05Ka, double[] P95Ka,
    RateInterval[] Rates, string Status, string? RejectReason, string ContentHash, string LineHash);

public sealed record ComputeResult(
    FailureKind? Failure,
    List<Diagnostic> Diagnostics,
    List<CandidateModel> Candidates,
    List<string> AffectedRunIds);

/// <summary>A maximal run of segments not interrupted by an explicit gap or unconformity.</summary>
public sealed record Run(string Id, string CoreId, List<SegmentMat> Segments, double TopM, double BottomM);

public sealed record BranchCtx(string Name, HashSet<string> ExtraExcludedAnchorIds);

public sealed record MaterialSet(
    IReadOnlyDictionary<string, CoreMat> Cores,
    IReadOnlyDictionary<string, SegmentMat> Segments,
    IReadOnlyDictionary<string, BoundaryMat> Boundaries,
    IReadOnlyDictionary<string, AnchorMat> Anchors,
    IReadOnlyDictionary<string, ReservoirOffsetMat> Offsets,
    IReadOnlyDictionary<string, EventLayerMat> EventLayers,
    IReadOnlyDictionary<string, ExclusionMat> Exclusions);

public static class Engine
{
    public const int DefaultBudget = 64;

    /// <summary>
    /// Builds continuous runs for one core. Depth gaps and unconformities are explicit
    /// boundaries; ages are only ever required to be monotonic inside a run.
    /// </summary>
    public static List<Run> BuildRuns(MaterialSet m, string coreId, List<Diagnostic> diags)
    {
        var valid = new List<(SegmentMat seg, double top, double bottom)>();
        foreach (var seg in m.Segments.Values.Where(s => s.CoreId == coreId).OrderBy(s => s.Id, StringComparer.Ordinal))
        {
            if (seg.Top.Normalized is null || seg.Bottom.Normalized is null)
            {
                diags.Add(new("unconvertible-depth", $"segment/{seg.Id}",
                    $"segment kept in raw form: {seg.Top.Error ?? seg.Bottom.Error}", "error"));
                continue;
            }
            var (t, b) = (seg.Top.Normalized.Value, seg.Bottom.Normalized.Value);
            if (t > b)
            {
                diags.Add(new("inverted-segment", $"segment/{seg.Id}",
                    $"top {t:0.###} m is below bottom {b:0.###} m; segment excluded from computation", "error"));
                continue;
            }
            valid.Add((seg, t, b));
        }
        valid.Sort((x, y) => x.top != y.top ? x.top.CompareTo(y.top)
            : string.CompareOrdinal(x.seg.Id, y.seg.Id));

        var boundaryByAfter = m.Boundaries.Values
            .Where(b => b.CoreId == coreId)
            .GroupBy(b => b.AfterSegmentId)
            .ToDictionary(g => g.Key, g => g.OrderBy(b => b.Id, StringComparer.Ordinal).Last());

        var runs = new List<Run>();
        var current = new List<SegmentMat>();
        double runTop = 0, runBottom = 0;
        foreach (var (seg, t, b) in valid)
        {
            if (current.Count == 0) { runTop = t; }
            current.Add(seg);
            runBottom = b;
            var kind = boundaryByAfter.TryGetValue(seg.Id, out var bd) ? bd.Kind : (BoundaryKind?)null;
            if (kind is null)
                diags.Add(new("missing-boundary", $"boundary/after:{seg.Id}",
                    $"no boundary recorded after {seg.Id}; treated as explicit gap", "warning"));
            if (kind is null or BoundaryKind.Gap or BoundaryKind.Unconformity)
            {
                runs.Add(new Run($"run/{coreId}/{current[0].Id}", coreId, current.ToList(), runTop, runBottom));
                current = new List<SegmentMat>();
            }
        }
        if (current.Count > 0)
            runs.Add(new Run($"run/{coreId}/{current[0].Id}", coreId, current.ToList(), runTop, runBottom));
        return runs;
    }

    public static ComputeResult Compute(
        MaterialSet m,
        IReadOnlyList<BranchCtx> branches,
        int paramVersion,
        int budget = DefaultBudget,
        HashSet<string>? onlyRunIds = null)
    {
        var diags = new List<Diagnostic>();
        var candidates = new List<CandidateModel>();
        var affected = new List<string>();
        var globallyExcluded = m.Exclusions.Values.Where(x => x.Active)
            .Select(x => x.AnchorId).ToHashSet(StringComparer.Ordinal);

        var coreIds = m.Cores.Keys.OrderBy(x => x, StringComparer.Ordinal).ToList();
        var anyRunWithAnchors = false;
        foreach (var coreId in coreIds)
        {
            var runs = BuildRuns(m, coreId, diags);
            foreach (var run in runs)
            {
                if (onlyRunIds is not null && !onlyRunIds.Contains(run.Id)) continue;
                affected.Add(run.Id);

                var anchors = new List<(AnchorMat a, double depth, AgeDist eff)>();
                foreach (var a in m.Anchors.Values.Where(x => x.CoreId == coreId)
                             .OrderBy(x => x.Id, StringComparer.Ordinal))
                {
                    if (a.Depth.Normalized is null)
                    {
                        diags.Add(new("unconvertible-anchor-depth", $"anchor/{a.Id}/depth",
                            $"anchor kept in raw form ('{a.Depth.Raw}'): {a.Depth.Error}", "error"));
                        continue;
                    }
                    var d = a.Depth.Normalized.Value;
                    if (d < run.TopM - 1e-9 || d > run.BottomM + 1e-9) continue;
                    var eff = a.Dist;
                    if (a.ReservoirOffsetId is not null)
                    {
                        if (m.Offsets.TryGetValue(a.ReservoirOffsetId, out var ro)) eff = eff.Add(ro.OffsetKa);
                        else diags.Add(new("missing-reservoir-offset", $"anchor/{a.Id}",
                            $"reservoir offset '{a.ReservoirOffsetId}' not found; anchor skipped", "error"));
                    }
                    anchors.Add((a, d, eff));
                }
                if (anchors.Count > 0) anyRunWithAnchors = true;
                if (anchors.Count < 2)
                {
                    if (anchors.Count == 1)
                        diags.Add(new("insufficient-anchors", run.Id,
                            $"run {run.Id} has only one usable anchor; no age model can be derived", "error"));
                    continue;
                }

                // Group ties: identical depths with distinct ages are并列锚点, never silently merged.
                var ordered = anchors.OrderBy(x => x.depth).ThenBy(x => x.a.Id, StringComparer.Ordinal).ToList();
                var groups = new List<List<(AnchorMat a, double depth, AgeDist eff)>>();
                foreach (var x in ordered)
                {
                    if (groups.Count > 0 && Math.Abs(groups[^1][0].depth - x.depth) < 1e-9) groups[^1].Add(x);
                    else groups.Add(new List<(AnchorMat, double, AgeDist)> { x });
                }
                foreach (var g in groups.Where(g => g.Count > 1))
                    diags.Add(new("depth-duplicate", $"anchor/{string.Join("+", g.Select(x => x.a.Id))}",
                        $"tied anchors at {g[0].depth:0.###} m: {string.Join(", ", g.Select(x => x.a.Id))}; all retained as alternatives", "info"));

                long combos = 1;
                foreach (var g in groups) { combos *= g.Count; if (combos > budget) break; }
                if (combos > budget)
                    return new ComputeResult(FailureKind.BudgetExhausted, diags, candidates, affected);

                foreach (var branch in branches)
                {
                    var excluded = new HashSet<string>(globallyExcluded, StringComparer.Ordinal);
                    excluded.UnionWith(branch.ExtraExcludedAnchorIds);
                    var usable = groups
                        .Select(g => g.Where(x => !excluded.Contains(x.a.Id)).ToList())
                        .Where(g => g.Count > 0).ToList();
                    if (usable.Count < 2) continue;

                    foreach (var choice in EnumerateChoices(usable, budget))
                    {
                        candidates.Add(BuildCandidate(m, branch.Name, run, choice, paramVersion, diags));
                        if (candidates.Count > budget * 4)
                            return new ComputeResult(FailureKind.BudgetExhausted, diags, candidates, affected);
                    }
                }
            }
        }

        // Equal-optimal models (identical geometry from different anchor choices) are all retained.
        foreach (var dup in candidates.Where(c => c.Status == "retained")
                     .GroupBy(c => c.Branch + "|" + c.RunId + "|" + c.LineHash).Where(g => g.Count() > 1))
            diags.Add(new("equal-optimal-retained", dup.Key.Split('|')[1],
                $"{dup.Count()} equal-optimal candidates retained: {string.Join(", ", dup.Select(c => c.Id))}", "info"));

        candidates.Sort((x, y) => string.CompareOrdinal(x.Id, y.Id));
        var failure = candidates.Count == 0 && anyRunWithAnchors
            ? FailureKind.SemanticUnsolvable
            : (FailureKind?)null;
        return new ComputeResult(failure, diags, candidates, affected);
    }

    static IEnumerable<List<(AnchorMat a, double depth, AgeDist eff)>> EnumerateChoices(
        List<List<(AnchorMat a, double depth, AgeDist eff)>> groups, int budget)
    {
        var result = new List<(AnchorMat, double, AgeDist)>();
        IEnumerable<List<(AnchorMat a, double depth, AgeDist eff)>> Rec(int i)
        {
            if (i == groups.Count) { yield return result.ToList(); yield break; }
            foreach (var opt in groups[i])
            {
                result.Add(opt);
                foreach (var r in Rec(i + 1)) yield return r;
                result.RemoveAt(result.Count - 1);
            }
        }
        var emitted = 0;
        foreach (var r in Rec(0)) { if (++emitted > budget) yield break; yield return r; }
    }

    static CandidateModel BuildCandidate(MaterialSet m, string branch, Run run,
        List<(AnchorMat a, double depth, AgeDist eff)> choice, int paramVersion, List<Diagnostic> diags)
    {
        var depths = choice.Select(c => c.depth).ToArray();
        var medians = choice.Select(c => c.eff.Median).ToArray();
        var p05 = choice.Select(c => c.eff.P05).ToArray();
        var p95 = choice.Select(c => c.eff.P95).ToArray();

        string? reject = null;
        for (var i = 1; i < medians.Length; i++)
        {
            if (medians[i] <= medians[i - 1])
            {
                reject = $"non-monotonic: {choice[i].a.Id}@{depths[i]:0.###}m ({medians[i]:0.###}ka) " +
                         $"<= {choice[i - 1].a.Id}@{depths[i - 1]:0.###}m ({medians[i - 1]:0.###}ka) within {run.Id}";
                break;
            }
        }

        var rates = new List<RateInterval>();
        if (reject is null)
        {
            for (var i = 1; i < medians.Length; i++)
            {
                var dd = depths[i] - depths[i - 1];
                if (dd <= 0) continue;
                rates.Add(new RateInterval(choice[i - 1].a.Id, choice[i].a.Id,
                    depths[i - 1], depths[i],
                    (medians[i] - medians[i - 1]) / dd,
                    Math.Max(0, (p05[i] - p95[i - 1]) / dd),
                    Math.Max(0, (p95[i] - p05[i - 1]) / dd)));
            }
        }

        var scope = new StringBuilder()
            .Append(branch).Append('|').Append(run.Id).Append('|').Append(paramVersion);
        foreach (var c in choice)
        {
            scope.Append('|').Append(c.a.Id).Append(':').Append(c.a.Version);
            if (c.a.ReservoirOffsetId is not null && m.Offsets.TryGetValue(c.a.ReservoirOffsetId, out var ro))
                scope.Append("@").Append(ro.Id).Append(':').Append(ro.Version);
        }
        var hash = ShortHash(scope.ToString());
        var lineHash = ShortHash(string.Join(",", depths.Zip(medians, (d, a) => $"{d:0.####}:{a:0.####}")));
        var id = $"cand/{branch}/{run.CoreId}/{run.Segments[0].Id}/{hash}";
        return new CandidateModel(id, branch, run.CoreId, run.Id,
            choice.Select(c => c.a.Id).ToArray(), depths, medians, p05, p95,
            rates.ToArray(), reject is null ? "retained" : "rejected", reject,
            ShortHash(scope + "|geom"), lineHash);
    }

    public static string ShortHash(string s)
        => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(s)))[..8].ToLowerInvariant();

    /// <summary>Linear interpolation of a candidate's median age at an arbitrary depth.</summary>
    public static double InterpMedian(CandidateModel c, double depthM)
    {
        var d = c.DepthsM; var a = c.MedianKa;
        if (depthM <= d[0]) return a[0];
        if (depthM >= d[^1]) return a[^1];
        for (var i = 1; i < d.Length; i++)
        {
            if (depthM <= d[i])
            {
                var t = (depthM - d[i - 1]) / (d[i] - d[i - 1]);
                return a[i - 1] * (1 - t) + a[i] * t;
            }
        }
        return a[^1];
    }
}
