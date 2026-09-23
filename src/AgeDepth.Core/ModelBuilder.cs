namespace AgeDepth.Core;

/// <summary>
/// Builds age-depth model candidates from anchors and explicit boundaries.
/// Rules enforced here (machine-checkable in the output):
///  - age is monotonic non-decreasing with depth inside a continuous segment only;
///  - gaps and unconformities are explicit boundaries: monotonicity never crosses them;
///  - an anchor distribution is sampled by quantiles, never replaced by its mean;
///  - all equally-good candidate clusters are retained (ties are not truncated).
/// </summary>
public static class ModelBuilder
{
    private const double Tol = 1e-9;

    public static ModelResult Build(ModelInput input, ModelParams p)
    {
        var diags = new List<Diagnostic>();
        var digest = Canonical.InputDigest(input);

        // Derived layer: unit normalisation. Unconvertible values stay in place with a diagnostic.
        var usable = new List<Anchor>();
        foreach (var a in input.Anchors.OrderBy(a => a.Depth.Value).ThenBy(a => a.Id, StringComparer.Ordinal))
        {
            if (input.ExcludedAnchorIds.Contains(a.Id))
            {
                diags.Add(new Diagnostic("AnchorExcluded", "info",
                    $"anchor {a.Id} excluded by recorded decision", a.Id, a.Depth.Value));
                continue;
            }
            if (!a.Depth.TryConvertTo("m", out var depth))
            {
                diags.Add(new Diagnostic("UnconvertibleUnit", "warning",
                    $"anchor {a.Id} depth unit '{a.Depth.Unit}' cannot be converted to m; value left in place and excluded from model",
                    a.Id, null));
                continue;
            }
            var dist = a.Dist;
            if (input.ReservoirOffsetsBySource.TryGetValue(a.Source, out var offset) && offset != 0)
                dist = dist.Shift(-offset); // reservoir offset makes apparent age older; subtract
            usable.Add(a with { Depth = depth, Dist = dist });
        }

        var intervals = ContinuousIntervals(input.CoreTopM, input.CoreBottomM, input.Boundaries);

        // Budget check: anchors per continuous segment.
        foreach (var (top, bottom, idx) in intervals)
        {
            var count = usable.Count(a => InInterval(a.Depth.Value, top, bottom, idx == 0));
            if (count > p.MaxAnchorsPerSegment)
            {
                diags.Add(new Diagnostic("BudgetExhausted", "error",
                    $"segment seg-{idx} has {count} anchors, budget is {p.MaxAnchorsPerSegment}",
                    null, null, $"seg-{idx}"));
                return ModelResult.Failure(ResultStatus.BudgetExhausted, diags, digest, p.Version);
            }
        }

        var segments = new List<SegmentResult>();
        foreach (var (top, bottom, idx) in intervals)
        {
            var segId = $"seg-{idx}";
            var anchors = usable.Where(a => InInterval(a.Depth.Value, top, bottom, idx == 0))
                                .OrderBy(a => a.Depth.Value).ThenBy(a => a.Id, StringComparer.Ordinal)
                                .ToList();
            segments.Add(BuildSegment(segId, top, bottom, anchors, p));
        }

        var anchored = segments.Where(s => s.Status != "empty").ToList();
        if (anchored.Count > 0 && anchored.All(s => s.Status is "inverted" or "unsolvable"))
        {
            diags.Add(new Diagnostic("SemanticallyUnsolvable", "error",
                "no continuous segment admits a monotonic age-depth path", null, null, null));
            return new ModelResult(ResultStatus.SemanticallyUnsolvable, segments.ToArray(),
                diags.ToArray(), digest, p.Version);
        }

        return new ModelResult(ResultStatus.Ok, segments.ToArray(), diags.ToArray(), digest, p.Version);
    }

    internal static bool InInterval(double d, double top, double bottom, bool isFirst) =>
        (isFirst ? d >= top - Tol : d > top + Tol) && d <= bottom + Tol;

    internal static List<(double Top, double Bottom, int Idx)> ContinuousIntervals(
        double coreTop, double coreBottom, IReadOnlyList<Boundary> boundaries)
    {
        var cuts = boundaries
            .OrderBy(b => b.TopM).ThenBy(b => b.Id, StringComparer.Ordinal)
            .Select(b => (Top: Math.Min(b.TopM, b.BottomM), Bottom: Math.Max(b.TopM, b.BottomM)))
            .ToList();
        var result = new List<(double, double, int)>();
        var cursor = coreTop;
        var idx = 0;
        foreach (var (bt, bb) in cuts)
        {
            if (bt > cursor + Tol) { result.Add((cursor, bt, idx++)); }
            cursor = Math.Max(cursor, bb);
        }
        if (coreBottom > cursor + Tol) result.Add((cursor, coreBottom, idx++));
        if (result.Count == 0 && coreBottom > coreTop) result.Add((coreTop, coreBottom, 0));
        return result;
    }

    private static SegmentResult BuildSegment(string segId, double top, double bottom,
        List<Anchor> anchors, ModelParams p)
    {
        var diags = new List<Diagnostic>();

        if (anchors.Count == 0)
            return Fin(new SegmentResult(segId, top, bottom, "empty",
                Array.Empty<CandidatePath>(), Array.Empty<BandPoint>(), Array.Empty<RateInterval>(),
                diags.ToArray(), "", 0));

        // Co-located anchors (duplicate depth): all retained, combined as a depth node.
        var groups = anchors.GroupBy(a => a.Depth.Value).OrderBy(g => g.Key).ToList();
        foreach (var g in groups.Where(g => g.Count() > 1))
            diags.Add(new Diagnostic("CoLocatedAnchors", "info",
                $"depth {Canonical.Num(g.Key)} m has {g.Count()} co-located anchors: {string.Join(",", g.Select(a => a.Id))}; combined as one depth node",
                null, g.Key, segId));

        if (groups.Count == 1)
            diags.Add(new Diagnostic("SingleAnchor", "info",
                "segment constrained by a single depth node; rates undetermined", null, groups[0].Key, segId));

        // Median inversion check (structured, positioned).
        var medians = groups.Select(g => g.Average(a => a.Dist.Median)).ToArray();
        var anyInversion = false;
        for (var i = 0; i + 1 < medians.Length; i++)
        {
            if (medians[i + 1] < medians[i] - Tol)
            {
                anyInversion = true;
                diags.Add(new Diagnostic("InvertedSection", "error",
                    $"median age decreases with depth between {Canonical.Num(groups[i].Key)} m and {Canonical.Num(groups[i + 1].Key)} m inside a continuous segment",
                    groups[i + 1].First().Id, groups[i + 1].Key, segId));
            }
        }

        // Ensemble of piecewise-linear paths via stratified quantile draws.
        var m = Math.Max(4, p.EnsembleSize);
        var validPaths = new List<double[]>();
        var rejected = 0;
        for (var j = 0; j < m; j++)
        {
            var q = (j + 0.5) / m;
            var ages = groups.Select(g => g.Average(a => a.Dist.Quantile(q))).ToArray();
            var ok = true;
            for (var i = 0; i + 1 < ages.Length; i++)
                if (ages[i + 1] < ages[i] - Tol) { ok = false; break; }
            if (ok) validPaths.Add(ages); else rejected++;
        }

        var depths = groups.Select(g => g.Key).ToArray();

        if (validPaths.Count == 0)
        {
            var status = anyInversion ? "inverted" : "unsolvable";
            if (!anyInversion)
                diags.Add(new Diagnostic("SemanticallyUnsolvable", "error",
                    "no monotonic path through anchor distributions in this segment", null, null, segId));
            var rejectedCand = new CandidatePath($"{segId}/rejected", depths.Zip(medians, (d, a) => new Knot(d, a)).ToArray(),
                0, "rejected", "all ensemble draws violate monotonicity");
            return Fin(new SegmentResult(segId, top, bottom, status,
                new[] { rejectedCand }, Array.Empty<BandPoint>(), Array.Empty<RateInterval>(),
                diags.ToArray(), "", rejected));
        }

        // Cluster valid paths into candidates; ties at the cutoff are all retained.
        var clusters = new Dictionary<string, List<double[]>>(StringComparer.Ordinal);
        foreach (var path in validPaths)
        {
            var key = string.Join(",", path.Select(a => Math.Round(a * 10) / 10.0));
            if (!clusters.TryGetValue(key, out var list)) clusters[key] = list = new List<double[]>();
            list.Add(path);
        }
        var ordered = clusters.OrderByDescending(kv => kv.Value.Count)
                              .ThenBy(kv => kv.Key, StringComparer.Ordinal).ToList();
        var keep = Math.Min(p.MaxCandidates, ordered.Count);
        if (keep < ordered.Count)
        {
            var cutoffWeight = ordered[keep - 1].Value.Count;
            while (keep < ordered.Count && ordered[keep].Value.Count == cutoffWeight) keep++;
        }

        var candidates = new List<CandidatePath>();
        var ci = 0;
        foreach (var kv in ordered.Take(keep))
        {
            var medoid = Medoid(kv.Value);
            candidates.Add(new CandidatePath($"{segId}/c{ci}",
                depths.Zip(medoid, (d, a) => new Knot(d, a)).ToArray(),
                (double)kv.Value.Count / validPaths.Count, "adopted", null));
            ci++;
        }
        if (rejected > 0)
            candidates.Add(new CandidatePath($"{segId}/rejected", Array.Empty<Knot>(), 0,
                "rejected", $"{rejected} ensemble draws violate monotonicity and were discarded"));

        var band = BuildBand(depths, validPaths, top, bottom);
        var rates = BuildRates(segId, depths, validPaths);

        return Fin(new SegmentResult(segId, top, bottom,
            groups.Count == 1 ? "underdetermined" : "ok",
            candidates.ToArray(), band, rates, diags.ToArray(), "", rejected));
    }

    private static SegmentResult Fin(SegmentResult seg) =>
        seg with { ResultHash = Canonical.SegmentHash(seg) };

    private static double[] Medoid(List<double[]> paths)
    {
        var n = paths[0].Length;
        var mean = new double[n];
        foreach (var path in paths) for (var i = 0; i < n; i++) mean[i] += path[i] / paths.Count;
        var best = paths[0]; var bestDist = double.MaxValue;
        foreach (var path in paths)
        {
            var d = 0.0;
            for (var i = 0; i < n; i++) { var t = path[i] - mean[i]; d += t * t; }
            if (d < bestDist) { bestDist = d; best = path; }
        }
        return best;
    }

    private static BandPoint[] BuildBand(double[] depths, List<double[]> paths, double top, double bottom)
    {
        const int gridN = 40;
        var band = new List<BandPoint>();
        for (var gi = 0; gi <= gridN; gi++)
        {
            var d = top + (bottom - top) * gi / gridN;
            var ages = paths.Select(path => Interp(depths, path, d)).OrderBy(v => v).ToArray();
            band.Add(new BandPoint(d, Percentile(ages, 0.05), Percentile(ages, 0.50), Percentile(ages, 0.95)));
        }
        return band.ToArray();
    }

    private static RateInterval[] BuildRates(string segId, double[] depths, List<double[]> paths)
    {
        var rates = new List<RateInterval>();
        for (var i = 0; i + 1 < depths.Length; i++)
        {
            var dd = depths[i + 1] - depths[i];
            if (dd <= Tol) continue;
            var rs = paths.Select(path => (path[i + 1], path[i]))
                          .Where(t => t.Item1 - t.Item2 > Tol)
                          .Select(t => dd / (t.Item1 - t.Item2))
                          .OrderBy(v => v).ToArray();
            if (rs.Length == 0) continue;
            rates.Add(new RateInterval(segId, depths[i], depths[i + 1],
                rs.First(), rs.Last(), Percentile(rs, 0.5)));
        }
        return rates.ToArray();
    }

    internal static double Interp(double[] xs, double[] ys, double x)
    {
        if (x <= xs[0]) return ys[0];
        if (x >= xs[^1]) return ys[^1];
        for (var i = 0; i + 1 < xs.Length; i++)
        {
            if (x <= xs[i + 1] + Tol)
            {
                var span = xs[i + 1] - xs[i];
                if (span <= Tol) return (ys[i] + ys[i + 1]) / 2.0;
                var t = (x - xs[i]) / span;
                return ys[i] + t * (ys[i + 1] - ys[i]);
            }
        }
        return ys[^1];
    }

    internal static double Percentile(double[] sorted, double q)
    {
        if (sorted.Length == 1) return sorted[0];
        var pos = q * (sorted.Length - 1);
        var lo = (int)Math.Floor(pos);
        var hi = Math.Min(lo + 1, sorted.Length - 1);
        return sorted[lo] + (sorted[hi] - sorted[lo]) * (pos - lo);
    }
}
