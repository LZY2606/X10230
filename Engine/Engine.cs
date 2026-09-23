using AgeModel.Domain;

namespace AgeModel.Engine;

/// <summary>Distinct run outcomes: ok / incomplete input / budget / semantic.</summary>
public enum RunStatus { Ok, Diagnostics, BudgetExhausted, Unsolvable }

public sealed record Diagnostic(string Position, string Severity, string Message);

/// <summary>Machine-readable rule judgement emitted during a build.</summary>
public sealed record RuleEvent(string Type, string Detail);

public sealed record AnchorPoint(string AnchorId, string SectionId, double Depth, double P05, double P50, double P95);

public sealed record RateInterval(string SectionId, double FromDepth, double ToDepth, double? Median, double? Lo, double? Hi);

public sealed record CandidateModelData(
    string Id, int Ord, List<AnchorPoint> Points, List<string> ExcludedAnchorIds,
    double Score, List<RateInterval> Rates);

public sealed record EngineParams(int Version, double ReservoirOffsetKa, bool AllowExclusions, int MaxCandidates, double MonotonicToleranceKa)
{
    public static EngineParams Default => new(1, 0.40, true, 32, 1e-6);
}

public sealed record BuildResult(
    RunStatus Status,
    string StatusDetail,
    List<CandidateModelData> Candidates,
    List<Diagnostic> Diagnostics,
    List<RuleEvent> RuleEvents,
    Dictionary<string, string> SectionDigests);

public static class Engine
{
    public static BuildResult Build(
        IReadOnlyList<Section> sections,
        IReadOnlyList<Anchor> anchors,
        IReadOnlyList<ExclusionDecision> recordedExclusions,
        EngineParams p)
    {
        var diags = new List<Diagnostic>();
        var rules = new List<RuleEvent>();
        var sectionDigests = new Dictionary<string, string>();

        var usable = new List<Anchor>();
        foreach (var a in anchors)
        {
            if (a.Depth.Value is null)
            {
                diags.Add(new Diagnostic($"anchors/{a.Id}/depth", "error",
                    $"depth not convertible from raw '{a.Depth.Raw}' (unit '{a.Depth.Unit}'); value kept in place, anchor excluded from computation"));
                continue;
            }
            if (a.Dist is null)
            {
                diags.Add(new Diagnostic($"anchors/{a.Id}/distribution", "error",
                    "age distribution missing or unconvertible; anchor excluded from computation"));
                continue;
            }
            if (double.IsNaN(a.Dist.P05) || double.IsNaN(a.Dist.Median) || double.IsNaN(a.Dist.P95))
            {
                diags.Add(new Diagnostic($"anchors/{a.Id}/distribution", "error",
                    "distribution yields no quantiles (empty sample set?); anchor excluded from computation"));
                continue;
            }
            usable.Add(a);
        }

        foreach (var a in usable.Where(x => x.Dist!.Kind == "samples"))
        {
            var mean = a.Dist!.MeanValue;
            var median = a.Dist.Median;
            if (Math.Abs(mean - median) > 1e-9)
                rules.Add(new RuleEvent("rule.distribution.preserved",
                    $"anchor {a.Id}: distribution retained as {a.Dist.Samples!.Length} samples; median {median:0.###} ka used, mean {mean:0.###} ka NOT substituted"));
        }

        var orderedSections = sections.OrderBy(s => s.Ord).ToList();
        foreach (var s in orderedSections.Where(s => s.BoundaryAbove is "gap" or "unconformity"))
            rules.Add(new RuleEvent("rule.boundary.explicit",
                $"section {s.Id}: {s.BoundaryAbove} at {(s.Top.Value?.ToString("0.###") ?? "?")} mbsf is an explicit boundary; age monotonicity is NOT enforced across it"));

        // Per-section depth groups -> branch combinations (depth duplicates / co-located anchors).
        var sectionCombos = new List<List<List<Anchor>>>();
        long totalCombos = 1;
        foreach (var s in orderedSections)
        {
            var sAnchors = usable.Where(a => a.SectionId == s.Id)
                .OrderBy(a => a.Depth.Value!.Value).ThenBy(a => a.Id, StringComparer.Ordinal).ToList();
            sectionDigests[s.Id] = SectionDigest(s, sAnchors, p);
            if (sAnchors.Count == 0)
            {
                sectionCombos.Add(new List<List<Anchor>>());
                continue;
            }
            var groups = sAnchors.GroupBy(a => a.Depth.Value!.Value)
                .OrderBy(g => g.Key)
                .Select(g => g.OrderBy(a => a.Id, StringComparer.Ordinal).ToList())
                .ToList();
            foreach (var g in groups.Where(g => g.Count > 1))
                rules.Add(new RuleEvent("rule.anchor.colocated",
                    $"section {s.Id}: depth {g[0].Depth.Value:0.###} mbsf holds {g.Count} anchors ({string.Join(", ", g.Select(a => a.Id))}); all retained as alternative branches"));
            var combos = Cartesian(groups);
            totalCombos *= combos.Count;
            if (totalCombos > p.MaxCandidates)
                return new BuildResult(RunStatus.BudgetExhausted,
                    $"candidate enumeration budget exhausted: {totalCombos} branch combinations exceed MaxCandidates={p.MaxCandidates}",
                    new(), diags, rules, sectionDigests);
            sectionCombos.Add(combos);
        }

        // Resolve monotonicity per combination; exclusions generate defensible variants.
        var perSectionVariants = new List<List<(List<AnchorPoint> Pts, List<string> Exc)>>();
        var unsolvableSections = new List<string>();
        for (var si = 0; si < orderedSections.Count; si++)
        {
            var s = orderedSections[si];
            var combos = sectionCombos[si];
            if (combos.Count == 0)
            {
                perSectionVariants.Add(new List<(List<AnchorPoint>, List<string>)> { (new(), new()) });
                continue;
            }
            var variants = new List<(List<AnchorPoint>, List<string>)>();
            foreach (var combo in combos)
                variants.AddRange(ResolveVariants(s, combo, recordedExclusions, p, rules));
            variants = variants
                .GroupBy(v => string.Join(",", v.Pts.Select(x => x.AnchorId)) + "|" + string.Join(",", v.Exc.OrderBy(x => x, StringComparer.Ordinal)))
                .Select(g => g.First()).ToList();
            if (variants.Count == 0)
                unsolvableSections.Add(s.Id);
            perSectionVariants.Add(variants);
        }

        if (unsolvableSections.Count > 0)
            return new BuildResult(RunStatus.Unsolvable,
                $"no admissible monotone age path in section(s) {string.Join(", ", unsolvableSections)}; exclusions {(p.AllowExclusions ? "could not resolve the inversion" : "are disabled by parameter")}",
                new(), diags, rules, sectionDigests);

        long totalCandidates = 1;
        foreach (var v in perSectionVariants) totalCandidates *= v.Count;
        if (totalCandidates > p.MaxCandidates)
            return new BuildResult(RunStatus.BudgetExhausted,
                $"candidate budget exhausted: {totalCandidates} admissible models exceed MaxCandidates={p.MaxCandidates}",
                new(), diags, rules, sectionDigests);

        var acc = new List<(List<AnchorPoint> Pts, List<string> Exc)> { (new(), new()) };
        foreach (var variants in perSectionVariants)
        {
            var next = new List<(List<AnchorPoint>, List<string>)>();
            foreach (var a in acc)
            foreach (var v in variants)
                next.Add((a.Pts.Concat(v.Pts).ToList(), a.Exc.Concat(v.Exc).ToList()));
            acc = next;
        }

        var candidates = acc
            .Select(c =>
            {
                var id = "cand-" + Hash.Sha(string.Join(">", c.Pts.Select(x => x.AnchorId)) + "|" + string.Join(",", c.Exc.OrderBy(x => x, StringComparer.Ordinal)))[..8];
                var score = c.Exc.Count * 10.0;
                return new CandidateModelData(id, 0, c.Pts, c.Exc.OrderBy(x => x, StringComparer.Ordinal).ToList(), score, ComputeRates(c.Pts));
            })
            .OrderBy(c => c.Score).ThenBy(c => c.Id, StringComparer.Ordinal)
            .ToList();
        for (var i = 0; i < candidates.Count; i++)
            candidates[i] = candidates[i] with { Ord = i };

        if (candidates.Count == 0)
            return new BuildResult(RunStatus.Diagnostics,
                "no usable anchors: input incomplete, nothing computed (no default path fabricated)",
                new(), diags, rules, sectionDigests);

        var best = candidates.Where(c => c.Score == candidates.Min(x => x.Score)).ToList();
        if (best.Count > 1)
            rules.Add(new RuleEvent("rule.candidates.retained",
                $"{best.Count} equal-best defensible models retained: {string.Join(", ", best.Select(c => c.Id))}"));

        return new BuildResult(RunStatus.Ok, "ok", candidates, diags, rules, sectionDigests);
    }

    static List<(List<AnchorPoint>, List<string>)> ResolveVariants(
        Section s, List<Anchor> combo, IReadOnlyList<ExclusionDecision> recorded, EngineParams p, List<RuleEvent> rules)
    {
        var pts = combo.Select(a => ToPoint(a, p)).ToList();
        if (IsMonotone(pts, p.MonotonicToleranceKa))
            return new List<(List<AnchorPoint>, List<string>)> { (pts, new()) };

        var violIdx = ViolatingIndices(pts, p.MonotonicToleranceKa);
        rules.Add(new RuleEvent("rule.monotonicity.violation",
            $"section {s.Id}: ages must be monotonic non-decreasing with depth inside an unbroken section; violated at {string.Join("; ", violIdx.Distinct().Select(i => $"{pts[i].AnchorId}@{pts[i].Depth:0.###}m={pts[i].P50:0.###}ka"))}"));

        var results = new List<(List<AnchorPoint>, List<string>)>();
        if (!p.AllowExclusions)
            return results;

        foreach (var i in violIdx.Distinct())
        {
            var kept = pts.Where((_, j) => j != i).ToList();
            if (IsMonotone(kept, p.MonotonicToleranceKa))
                results.Add((kept, new List<string> { pts[i].AnchorId }));
        }
        var recordedIds = recorded.Select(r => r.AnchorId).ToHashSet(StringComparer.Ordinal);
        var recordedHere = pts.Where(x => recordedIds.Contains(x.AnchorId)).Select(x => x.AnchorId).ToList();
        if (recordedHere.Count > 0)
        {
            var kept = pts.Where(x => !recordedIds.Contains(x.AnchorId)).ToList();
            if (kept.Count > 0 && IsMonotone(kept, p.MonotonicToleranceKa))
                results.Add((kept, recordedHere));
        }
        return results;
    }

    static AnchorPoint ToPoint(Anchor a, EngineParams p)
    {
        var d = a.Dist!;
        var off = a.Marine ? p.ReservoirOffsetKa : 0.0;
        return new AnchorPoint(a.Id, a.SectionId, a.Depth.Value!.Value,
            d.P05 - off, d.Median - off, d.P95 - off);
    }

    static bool IsMonotone(List<AnchorPoint> pts, double tol)
    {
        for (var i = 0; i + 1 < pts.Count; i++)
            if (pts[i + 1].P50 < pts[i].P50 - tol) return false;
        return true;
    }

    static List<int> ViolatingIndices(List<AnchorPoint> pts, double tol)
    {
        var idx = new List<int>();
        for (var i = 0; i + 1 < pts.Count; i++)
            if (pts[i + 1].P50 < pts[i].P50 - tol) { idx.Add(i); idx.Add(i + 1); }
        return idx;
    }

    static List<RateInterval> ComputeRates(List<AnchorPoint> pts)
    {
        var rates = new List<RateInterval>();
        foreach (var g in pts.GroupBy(p => p.SectionId))
        {
            var sp = g.OrderBy(p => p.Depth).ToList();
            for (var i = 0; i + 1 < sp.Count; i++)
            {
                var a = sp[i]; var b = sp[i + 1];
                var dd = (b.Depth - a.Depth) * 100.0; // cm
                double? med = b.P50 - a.P50 > 0 ? dd / (b.P50 - a.P50) : null;
                double? lo = b.P95 - a.P05 > 0 ? dd / (b.P95 - a.P05) : null;
                double? hi = b.P05 - a.P95 > 0 ? dd / (b.P05 - a.P95) : null;
                rates.Add(new RateInterval(g.Key, a.Depth, b.Depth, med, lo, hi));
            }
        }
        return rates;
    }

    static List<List<Anchor>> Cartesian(List<List<Anchor>> groups)
    {
        var acc = new List<List<Anchor>> { new() };
        foreach (var g in groups)
            acc = acc.SelectMany(a => g.Select(x => a.Concat(new[] { x }).ToList())).ToList();
        return acc;
    }

    static string SectionDigest(Section s, List<Anchor> anchors, EngineParams p)
    {
        var sb = new System.Text.StringBuilder();
        sb.Append(s.Id).Append('|')
          .Append(s.Top.Value?.ToString("0.######") ?? s.Top.Raw).Append('|')
          .Append(s.Bottom.Value?.ToString("0.######") ?? s.Bottom.Raw).Append('|')
          .Append(s.BoundaryAbove).Append('|')
          .Append(p.ReservoirOffsetKa.ToString("0.######")).Append('|')
          .Append(p.MonotonicToleranceKa.ToString("0.######"));
        foreach (var a in anchors.OrderBy(x => x.Id, StringComparer.Ordinal))
            sb.Append('|').Append(a.Id).Append(':')
              .Append(a.Depth.Value?.ToString("0.######") ?? a.Depth.Raw).Append(':')
              .Append(a.Dist?.Canonical ?? "none").Append(':')
              .Append(a.Marine ? "m" : "t");
        return Hash.Sha(sb.ToString());
    }
}
