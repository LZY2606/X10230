using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using AgeDepth.Domain;

namespace AgeDepth.Engine;

/// <summary>Immutable projected inputs the engine reads. Built by the store's projector.</summary>
public sealed record EngineInput(
    IReadOnlyList<DepthDatum> Datums,
    IReadOnlyList<CoreSection> Sections,
    IReadOnlyList<StratBoundary> Boundaries,
    IReadOnlyList<AgeAnchor> Anchors,
    IReadOnlyList<ReservoirOffset> Offsets,
    IReadOnlyList<EventLayer> EventLayers,
    IReadOnlyList<ExclusionRecord> Exclusions,
    string ParamVersion,
    double MonotonicToleranceYr);

public sealed record EngineOutput(
    IReadOnlyList<SegmentResult> Segments,
    IReadOnlyList<Diagnostic> GlobalDiagnostics);

/// <summary>
/// Age-depth engine. Rules:
///  - age is monotonic non-decreasing with depth only inside a continuous segment;
///  - depth gaps and unconformities are explicit boundaries, never interpolated across;
///  - an anchor's distribution is used as quantiles, never collapsed to its mean;
///  - all equally-good monotonic candidates are retained.
/// </summary>
public sealed class AgeDepthEngine
{
    public const int DefaultBudget = 20_000;

    public EngineOutput Compute(EngineInput input, int budget = DefaultBudget)
    {
        var global = new List<Diagnostic>();
        var segments = Segment(input.Sections, input.Boundaries);
        var adopted = new HashSet<string>(input.Exclusions.Where(e => e.Adopted).Select(e => e.TargetId));
        var results = new List<SegmentResult>();

        foreach (var seg in segments)
        {
            var anchors = new List<(string id, double depth, AgeDist dist, string raw)>();
            var diags = new List<Diagnostic>();

            foreach (var a in input.Anchors.Where(a => a.DepthM >= seg.top && a.DepthM <= seg.bottom && !adopted.Contains(a.Id)))
            {
                var dist = a.Dist ?? Normalize(a, input.Offsets, diags);
                if (dist is null)
                {
                    // Unconvertible value stays in place with a diagnostic; it is not dropped silently
                    // and no default is substituted.
                    diags.Add(new Diagnostic("unconvertible-age", "error",
                        $"anchor:{a.Id}@{a.DepthM:F2}m",
                        $"Age of anchor {a.Id} ('{a.RawAge}') cannot be normalized; kept in place, excluded from computation."));
                    continue;
                }
                anchors.Add((a.Id, a.DepthM, ApplyReservoir(dist, a, input.Offsets), a.RawAge));
            }
            foreach (var e in input.EventLayers.Where(e => e.DepthM >= seg.top && e.DepthM <= seg.bottom && !adopted.Contains(e.Id)))
            {
                if (e.Dist is null)
                {
                    diags.Add(new Diagnostic("unconvertible-age", "error",
                        $"event:{e.Id}@{e.DepthM:F2}m",
                        $"Event layer {e.Id} ('{e.RawAge}') has no normalized distribution."));
                    continue;
                }
                anchors.Add((e.Id, e.DepthM, e.Dist, e.RawAge));
            }

            // Stable ordering: equivalent inputs always yield the same order.
            anchors.Sort((x, y) => x.depth != y.depth ? x.depth.CompareTo(y.depth) : string.CompareOrdinal(x.id, y.id));

            for (int i = 0; i + 1 < anchors.Count; i++)
                if (anchors[i].depth == anchors[i + 1].depth)
                    diags.Add(new Diagnostic("depth-duplicate", "warning",
                        $"anchor:{anchors[i].id},{anchors[i + 1].id}@{anchors[i].depth:F2}m",
                        $"Anchors {anchors[i].id} and {anchors[i + 1].id} share depth {anchors[i].depth:F2} m; both retained as alternatives."));

            var digest = Digest(seg, anchors, input.ParamVersion);
            var key = SegmentKey(seg);
            var (status, candidates, enumDiags) = Enumerate(key, anchors, input.MonotonicToleranceYr, budget);
            diags.AddRange(enumDiags);
            results.Add(new SegmentResult(key, digest, input.ParamVersion, status, candidates, diags));
        }
        return new EngineOutput(results, global);
    }

    // ---------- segmentation ----------

    internal static List<(double top, double bottom, string coreId)> Segment(
        IReadOnlyList<CoreSection> sections, IReadOnlyList<StratBoundary> boundaries)
    {
        var segs = new List<(double, double, string)>();
        foreach (var s in sections.OrderBy(s => s.TopM).ThenBy(s => s.Id, StringComparer.Ordinal))
        {
            var cuts = boundaries
                .Where(b => b.DepthM > s.TopM && b.DepthM < s.BottomM)
                .Select(b => b.DepthM).Distinct().OrderBy(d => d).ToList();
            double start = s.TopM;
            foreach (var c in cuts) { segs.Add((start, c, s.CoreId)); start = c; }
            segs.Add((start, s.BottomM, s.CoreId));
        }
        return segs;
    }

    public static string SegmentKey((double top, double bottom, string coreId) seg)
        => $"seg:{seg.coreId}:{seg.top:F3}-{seg.bottom:F3}";

    // ---------- normalization (derived layer only) ----------

    /// <summary>Parse raw age representations. Returns null when unconvertible.</summary>
    internal static AgeDist? Normalize(AgeAnchor a, IReadOnlyList<ReservoirOffset> offsets, List<Diagnostic> diags)
    {
        var raw = a.RawAge.Trim();
        // symmetric form: "2450 ± 60 14C yr BP"
        var m = System.Text.RegularExpressions.Regex.Match(raw,
            @"^(?<mean>\d+(\.\d+)?)\s*±\s*(?<sigma>\d+(\.\d+)?)\s*(?<unit>.*)$");
        if (m.Success)
        {
            double mean = double.Parse(m.Groups["mean"].Value, System.Globalization.CultureInfo.InvariantCulture);
            double sigma = double.Parse(m.Groups["sigma"].Value, System.Globalization.CultureInfo.InvariantCulture);
            return new AgeDist(mean - 1.645 * sigma, mean - 0.674 * sigma, mean, mean + 0.674 * sigma,
                mean + 1.645 * sigma, "symmetric", mean);
        }
        // explicit quantile form: "q: p5=4980 p25=5060 p50=5120 p75=5260 p95=6400 tail=long-tail-old cal yr BP"
        var q = System.Text.RegularExpressions.Regex.Match(raw,
            @"^q:\s*p5=(?<p5>[\d.]+)\s+p25=(?<p25>[\d.]+)\s+p50=(?<p50>[\d.]+)\s+p75=(?<p75>[\d.]+)\s+p95=(?<p95>[\d.]+)\s+tail=(?<tail>\S+)");
        if (q.Success)
        {
            double G(string n) => double.Parse(q.Groups[n].Value, System.Globalization.CultureInfo.InvariantCulture);
            return new AgeDist(G("p5"), G("p25"), G("p50"), G("p75"), G("p95"), q.Groups["tail"].Value, null);
        }
        return null;
    }

    private static AgeDist ApplyReservoir(AgeDist d, AgeAnchor a, IReadOnlyList<ReservoirOffset> offsets)
    {
        var off = offsets.FirstOrDefault(o => o.Id == a.ReservoirOffsetId);
        if (off is null) return d;
        // Marine reservoir correction: subtract offset, widen quantiles by offset sigma.
        return d with
        {
            P5 = d.P5 - off.MeanYr - off.SigmaYr,
            P25 = d.P25 - off.MeanYr,
            P50 = d.P50 - off.MeanYr,
            P75 = d.P75 - off.MeanYr,
            P95 = d.P95 - off.MeanYr + off.SigmaYr,
            Mean = d.Mean is null ? null : d.Mean - off.MeanYr
        };
    }

    // ---------- candidate enumeration ----------

    private static (ComputeStatus, IReadOnlyList<CandidateModel>, IReadOnlyList<Diagnostic>) Enumerate(
        string segKey, List<(string id, double depth, AgeDist dist, string raw)> anchors, double tol, int budget)
    {
        var diags = new List<Diagnostic>();
        if (anchors.Count < 2)
            return (ComputeStatus.NoSolution, Array.Empty<CandidateModel>(),
                new[] { new Diagnostic("insufficient-anchors", "error", $"segment:{segKey}",
                    $"Segment {segKey} has {anchors.Count} usable anchor(s); at least 2 are required.") });

        // Depth groups: anchors sharing a depth are alternatives, never used together.
        var groups = anchors.GroupBy(a => a.depth).OrderBy(g => g.Key).Select(g => g.ToList()).ToList();
        var best = new List<List<(string id, double depth, AgeDist dist, string raw)>>();
        int bestLen = 0;
        long steps = 0;
        bool exhausted = false;

        // DFS over selections: pick at most one anchor per depth group, medians non-decreasing (± tol).
        void Dfs(int gi, List<(string id, double depth, AgeDist dist, string raw)> cur)
        {
            if (exhausted) return;
            if (++steps > budget) { exhausted = true; return; }
            if (gi == groups.Count)
            {
                if (cur.Count > bestLen) { bestLen = cur.Count; best = new() { new(cur) }; }
                else if (cur.Count == bestLen && best.Count < 64) best.Add(new(cur));
                return;
            }
            foreach (var opt in groups[gi])
            {
                if (cur.Count == 0 || opt.dist.P50 >= cur[^1].dist.P50 - tol)
                {
                    cur.Add(opt); Dfs(gi + 1, cur); cur.RemoveAt(cur.Count - 1);
                }
            }
            Dfs(gi + 1, cur); // skip this depth group
        }
        Dfs(0, new());

        if (exhausted)
            return (ComputeStatus.BudgetExhausted, Array.Empty<CandidateModel>(),
                new[] { new Diagnostic("budget-exhausted", "error", $"segment:{segKey}",
                    $"Candidate enumeration in {segKey} exceeded budget of {budget} steps.") });

        if (bestLen < 2)
        {
            diags.Add(new Diagnostic("inverted-segment", "error", $"segment:{segKey}",
                $"No monotonic anchor pair exists in {segKey}; segment is stratigraphically inverted or ages conflict."));
            return (ComputeStatus.NoSolution, Array.Empty<CandidateModel>(), diags);
        }

        var used = new HashSet<string>(best.SelectMany(c => c.Select(a => a.id)));
        foreach (var a in anchors.Where(a => !used.Contains(a.id)))
            diags.Add(new Diagnostic("anchor-dropped", "warning", $"anchor:{a.id}@{a.depth:F2}m",
                $"Anchor {a.id} (median {a.dist.P50:F0} cal BP at {a.depth:F2} m) violates monotonicity in every maximal candidate; kept as material, dropped from models."));

        if (best.Count > 1)
            diags.Add(new Diagnostic("multiple-candidates", "info", $"segment:{segKey}",
                $"{best.Count} equally defensible candidates retained for {segKey}."));

        var candidates = new List<CandidateModel>();
        int ci = 0;
        foreach (var sel in best.OrderBy(c => string.Join(",", c.Select(a => a.id)), StringComparer.Ordinal))
        {
            var points = MonotonicBand(sel);
            var rates = Rates(sel);
            var dropped = anchors.Where(a => !sel.Any(s => s.id == a.id))
                .Select(a => new AnchorRef(a.id, a.depth)).ToList();
            candidates.Add(new CandidateModel(
                $"{segKey}#c{++ci}", segKey,
                sel.Select(a => new AnchorRef(a.id, a.depth)).ToList(), dropped,
                points, rates,
                $"Monotonic selection through {string.Join(", ", sel.Select(a => a.id))}."));
        }
        return (ComputeStatus.Ok, candidates, diags);
    }

    /// <summary>Monotonized uncertainty band from quantiles; medians are never replaced by means.</summary>
    private static List<ModelPoint> MonotonicBand(List<(string id, double depth, AgeDist dist, string raw)> sel)
    {
        var pts = new List<ModelPoint>();
        double lo = 0, hi = 0;
        foreach (var a in sel)
        {
            lo = Math.Max(lo, a.dist.P5);
            hi = Math.Max(hi, a.dist.P95);
            pts.Add(new ModelPoint(a.depth, lo, a.dist.P50, hi));
        }
        return pts;
    }

    private static List<RateInterval> Rates(List<(string id, double depth, AgeDist dist, string raw)> sel)
    {
        var rates = new List<RateInterval>();
        for (int i = 0; i + 1 < sel.Count; i++)
        {
            var (u, l) = (sel[i], sel[i + 1]);
            double dd = l.depth - u.depth;
            if (dd <= 0) continue;
            rates.Add(new RateInterval(u.depth, l.depth,
                Math.Max(0, (l.dist.P5 - u.dist.P95) / dd),
                Math.Max(0, (l.dist.P95 - u.dist.P5) / dd)));
        }
        return rates;
    }

    private static string Digest((double top, double bottom, string coreId) seg,
        List<(string id, double depth, AgeDist dist, string raw)> anchors, string paramVersion)
    {
        var sb = new StringBuilder();
        sb.Append(paramVersion).Append('|').Append(seg.top).Append('|').Append(seg.bottom);
        foreach (var a in anchors)
            sb.Append('|').Append(a.id).Append(':').Append(a.depth).Append(':').Append(a.raw)
              .Append(':').Append(a.dist.P5).Append(',').Append(a.dist.P50).Append(',').Append(a.dist.P95);
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(sb.ToString())))[..16];
    }

    public static string CanonicalJson<T>(T value) =>
        JsonSerializer.Serialize(value, new JsonSerializerOptions { WriteIndented = false });
}
