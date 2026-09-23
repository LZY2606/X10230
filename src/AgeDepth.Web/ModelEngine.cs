using System.Text.Json;

namespace AgeDepth;

public record EngineParams(string Version, double MonotonicToleranceKa, int CandidateBudget, int BandSteps);

public record CandidateModel(
    string Id, string SegmentId,
    string[] UsedAnchorIds, string[] ExcludedAnchorIds,
    string Status, // "accepted" | "rejected"
    double[] DepthKnots, double[] AgeMedian, double[] AgeP05, double[] AgeP95,
    RateInterval[] Rates, string Reason);

public record RateInterval(double TopDepth, double BaseDepth, double MedianKaPerM, double MinKaPerM, double MaxKaPerM);

public record SegmentResult(string SegmentId, bool Recomputed, string InputHash,
    List<CandidateModel> Candidates, List<Diagnostic> Diagnostics);

public record ComputationResult(ResultKind Kind, List<SegmentResult> Segments, List<Diagnostic> Diagnostics)
{
    public IEnumerable<CandidateModel> AllCandidates => Segments.SelectMany(s => s.Candidates);
}

/// <summary>
/// Age-depth engine. Rules encoded as machine-readable judgments:
///  R1 age is monotonic only inside continuous segments; gaps/unconformities are explicit barriers.
///  R2 an anchor distribution is never replaced by its mean; bands come from quantiles.
///  R3 all equi-optimal candidates are retained, never silently collapsed.
/// </summary>
public static class ModelEngine
{
    public static EngineParams DefaultParams => new("params/v1", 0.0, 64, 40);

    public static string SegmentInputHash(CoreSegment seg, List<AgeAnchor> anchors, EngineParams p)
    {
        var payload = seg.Json() + string.Join("|", anchors
            .OrderBy(a => a.Depth.Value ?? double.MaxValue).ThenBy(a => a.Id, StringComparer.Ordinal)
            .Select(a => a.Json())) + p.Version + p.MonotonicToleranceKa;
        return Hashing.Sha256(payload);
    }

    private static string Json<T>(this T v) => System.Text.Json.JsonSerializer.Serialize(v, Hashing.Json);

    /// <summary>
    /// Compute candidates for every continuous segment. <paramref name="previousHashes"/>
    /// enables local recompute: segments whose input hash is unchanged are marked
    /// Recomputed=false and carry no candidates (caller reuses prior results).
    /// </summary>
    public static ComputationResult Compute(
        IReadOnlyList<CoreSegment> segments,
        IReadOnlyList<AgeAnchor> anchors,
        EngineParams p,
        IReadOnlyDictionary<string, string>? previousHashes = null)
    {
        var resultDiagnostics = new List<Diagnostic>();
        var segmentResults = new List<SegmentResult>();
        var worst = ResultKind.Ok;

        foreach (var seg in segments.OrderBy(s => s.Top.Value ?? double.MaxValue).ThenBy(s => s.Id, StringComparer.Ordinal))
        {
            if (seg.IsBarrier) continue;
            var diags = new List<Diagnostic>();
            var segAnchors = anchors.Where(a => a.SegmentId == seg.Id).ToList();
            var hash = SegmentInputHash(seg, segAnchors, p);

            if (previousHashes != null && previousHashes.TryGetValue(seg.Id, out var old) && old == hash)
            {
                segmentResults.Add(new SegmentResult(seg.Id, false, hash, new(), new()));
                continue;
            }

            var usable = new List<AgeAnchor>();
            foreach (var a in segAnchors.OrderBy(a => a.Depth.Value ?? double.MaxValue).ThenBy(a => a.Id, StringComparer.Ordinal))
            {
                if (!a.Depth.Convertible || a.Depth.Value is null)
                {
                    diags.Add(new Diagnostic("UNCONVERTIBLE_DEPTH",
                        $"anchor depth '{a.Depth.Raw}' kept in place, not converted", seg.Id, null, a.Id));
                    continue;
                }
                usable.Add(a);
            }

            var (kind, candidates, genDiags) = GenerateCandidates(seg, usable, p);
            diags.AddRange(genDiags);
            if (kind > worst && kind != ResultKind.Conflict) worst = kind;
            segmentResults.Add(new SegmentResult(seg.Id, true, hash, candidates, diags));
        }

        return new ComputationResult(worst, segmentResults, resultDiagnostics);
    }

    private static (ResultKind, List<CandidateModel>, List<Diagnostic>) GenerateCandidates(
        CoreSegment seg, List<AgeAnchor> anchors, EngineParams p)
    {
        var diags = new List<Diagnostic>();
        var candidates = new List<CandidateModel>();

        if (anchors.Count == 0)
            return (ResultKind.Ok, candidates, diags);

        // Depth duplicates -> tied anchors (并列锚点): each tie choice branches a candidate.
        var tieGroups = anchors.GroupBy(a => a.Depth.Value!.Value).Where(g => g.Count() > 1).ToList();
        foreach (var g in tieGroups)
            diags.Add(new Diagnostic("DEPTH_DUPLICATE",
                $"{g.Count()} anchors share depth {g.Key}; kept as tied alternatives", seg.Id, g.Key, null));

        // Build anchor-choice combinations (cartesian over tie groups), capped by budget.
        var combos = new List<List<AgeAnchor>> { new() };
        foreach (var group in anchors.GroupBy(a => a.Depth.Value!.Value).OrderBy(g => g.Key))
        {
            var next = new List<List<AgeAnchor>>();
            foreach (var combo in combos)
            foreach (var choice in group.OrderBy(a => a.Id, StringComparer.Ordinal))
            {
                if (next.Count >= p.CandidateBudget)
                    return (ResultKind.BudgetExhausted, candidates,
                        diags.Append(new Diagnostic("BUDGET_EXHAUSTED",
                            "tie-combination budget exhausted", seg.Id, group.Key, null)).ToList());
                next.Add(combo.Append(choice).ToList());
            }
            combos = next;
        }

        // For each combination, enforce monotonicity; on inversion, branch by excluding
        // each conflicting anchor. All accepted AND rejected candidates are retained.
        foreach (var combo in combos)
        {
            var ordered = combo.OrderBy(a => a.Depth.Value!.Value).ThenBy(a => a.Id, StringComparer.Ordinal).ToList();
            var inversions = FindInversions(ordered, p.MonotonicToleranceKa);
            if (inversions.Count == 0)
            {
                candidates.Add(Build(seg, ordered, Array.Empty<AgeAnchor>(), "accepted", "monotonic"));
                continue;
            }

            diags.AddRange(inversions.Select(iv => new Diagnostic("INVERTED_SECTION",
                $"age decreases with depth between {iv.Item1.Id} and {iv.Item2.Id}",
                seg.Id, iv.Item2.Depth.Value, iv.Item2.Id)));

            // The inverted ordering itself is kept as a rejected candidate (evidence).
            candidates.Add(Build(seg, ordered, Array.Empty<AgeAnchor>(), "rejected", "non-monotonic as-is"));

            var anyAccepted = false;
            foreach (var conflictAnchor in inversions.SelectMany(t => new[] { t.Item1, t.Item2 }).DistinctBy(a => a.Id))
            {
                if (candidates.Count >= p.CandidateBudget)
                    return (ResultKind.BudgetExhausted, candidates,
                        diags.Append(new Diagnostic("BUDGET_EXHAUSTED",
                            "exclusion-candidate budget exhausted", seg.Id, null, null)).ToList());
                var reduced = ordered.Where(a => a.Id != conflictAnchor.Id).ToList();
                if (reduced.Count == 0) continue;
                if (FindInversions(reduced, p.MonotonicToleranceKa).Count == 0)
                {
                    candidates.Add(Build(seg, reduced, new[] { conflictAnchor }, "accepted",
                        $"excluded {conflictAnchor.Id} to restore monotonicity"));
                    anyAccepted = true;
                }
            }

            if (!anyAccepted && !candidates.Any(c => c.Status == "accepted"))
                diags.Add(new Diagnostic("SEMANTIC_UNSOLVABLE",
                    "no monotonic candidate reachable by single-anchor exclusion", seg.Id, null, null));
        }

        var kind = diags.Any(d => d.Code == "SEMANTIC_UNSOLVABLE") ? ResultKind.SemanticUnsolvable : ResultKind.Ok;
        // Stable order for equivalent input: accepted first, then by used-anchor ids.
        var orderedCandidates = candidates
            .OrderBy(c => c.Status == "accepted" ? 0 : 1)
            .ThenBy(c => string.Join(",", c.UsedAnchorIds), StringComparer.Ordinal)
            .ToList();
        return (kind, orderedCandidates, diags);
    }

    private static List<(AgeAnchor, AgeAnchor)> FindInversions(List<AgeAnchor> ordered, double tol)
    {
        var bad = new List<(AgeAnchor, AgeAnchor)>();
        for (var i = 0; i + 1 < ordered.Count; i++)
        {
            var a0 = Corrected(ordered[i]).Median;
            var a1 = Corrected(ordered[i + 1]).Median;
            if (a0 > a1 + tol) bad.Add((ordered[i], ordered[i + 1]));
        }
        return bad;
    }

    /// <summary>Reservoir offset shifts the whole distribution; the raw values stay untouched.</summary>
    private static AgeDistribution Corrected(AgeAnchor a)
    {
        var offset = a.ReservoirOffset is { Convertible: true, Value: not null } ? a.ReservoirOffset.Value.Value : 0.0;
        if (offset == 0.0) return a.Age;
        return a.Age with { Values = a.Age.Values.Select(v => v - offset).ToArray() };
    }

    private static CandidateModel Build(CoreSegment seg, List<AgeAnchor> used, IReadOnlyCollection<AgeAnchor> excluded,
        string status, string reason)
    {
        var depths = used.Select(a => a.Depth.Value!.Value).ToArray();
        var dists = used.Select(Corrected).ToArray();
        var med = dists.Select(d => d.Quantile(0.5)).ToArray();
        var p05 = dists.Select(d => d.Quantile(0.05)).ToArray();
        var p95 = dists.Select(d => d.Quantile(0.95)).ToArray();

        var rates = new List<RateInterval>();
        for (var i = 0; i + 1 < depths.Length; i++)
        {
            var dz = depths[i + 1] - depths[i];
            if (dz <= 0) continue;
            var medRate = (med[i + 1] - med[i]) / dz;
            var minRate = (p05[i + 1] - p95[i]) / dz;
            var maxRate = (p95[i + 1] - p05[i]) / dz;
            rates.Add(new RateInterval(depths[i], depths[i + 1], medRate,
                Math.Min(minRate, maxRate), Math.Max(minRate, maxRate)));
        }

        var id = "cand-" + Hashing.Sha256(seg.Id + string.Join(",", used.Select(a => a.Id)) +
            string.Join(",", excluded.Select(a => a.Id)) + status);
        return new CandidateModel(id, seg.Id,
            used.Select(a => a.Id).ToArray(),
            excluded.Select(a => a.Id).OrderBy(x => x, StringComparer.Ordinal).ToArray(),
            status, depths, med, p05, p95, rates.ToArray(), reason);
    }
}
