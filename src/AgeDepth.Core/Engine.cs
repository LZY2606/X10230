using System.Text.Json.Nodes;

namespace AgeDepth.Core;

/// <summary>
/// Age-depth engine. Rules enforced here (server side, unit-testable):
///  - age is monotonic only inside an unbroken segment; gaps and unconformities are explicit boundaries;
///  - an anchor's distribution is never replaced by its mean;
///  - every equiprobable candidate model is retained.
/// </summary>
public static class Engine
{
    private const int NodeCount = 41;

    public static ComputeResult Compute(ModelInput input, EngineParams p)
    {
        var diags = new List<Diagnostic>();
        var rules = new List<RuleEvent>();

        if (input.Anchors.Count > p.MaxAnchors)
            return ComputeResult.Failed(FailureKind.ResourceBudgetExhausted,
                "budget.anchors", $"anchor count {input.Anchors.Count} exceeds budget {p.MaxAnchors}", "input.anchors");

        var normalized = Normalize(input, p, diags, rules);
        foreach (var l in input.EventLayers)
        {
            if (Units.DepthToMetres(l.Unit) is null)
                diags.Add(new Diagnostic("unit.unconvertible",
                    $"event layer '{l.Id}' depth unit '{l.Unit}' cannot be converted; layer kept at raw position",
                    $"eventLayers/{l.Id}"));
        }
        var digest = InputDigest(normalized, input.ExcludedAnchorIds);

        var segments = new List<SegmentModel>();
        foreach (var seg in normalized.Segments)
        {
            if (seg.Boundary == "gap")
                rules.Add(new RuleEvent("rule.gap-explicit-boundary", seg.Id,
                    "depth gap is an explicit boundary; monotonicity is not enforced across it"));
            if (seg.Boundary == "unconformity")
                rules.Add(new RuleEvent("rule.unconformity-explicit-boundary", seg.Id,
                    "unconformity is an explicit boundary; monotonicity is not enforced across it"));
            segments.Add(ComputeSegment(seg, digest, p, rules));
        }

        var output = new ModelOutput(digest, p.Version, segments, diags, rules);
        var anyOk = segments.Any(s => s.Status == "ok");
        var result = new ComputeResult { Output = output, Failure = anyOk ? FailureKind.None : FailureKind.SemanticUnsolvable };
        foreach (var d in diags) result.Diagnostics.Add(d);
        if (!anyOk)
            result.Diagnostics.Add(new Diagnostic("model.unsolvable", "no segment produced a valid monotone model", "model"));
        return result;
    }

    private sealed record NSegment(string Id, double Top, double Base, string Boundary, List<NAnchor> Anchors);
    private sealed record NAnchor(string Id, double Depth, double[] SamplesKa, string Raw);
    private sealed record NInput(List<NSegment> Segments);

    private static NInput Normalize(ModelInput input, EngineParams p, List<Diagnostic> diags, List<RuleEvent> rules)
    {
        var excluded = new HashSet<string>(input.ExcludedAnchorIds, StringComparer.Ordinal);
        var anchors = new List<NAnchor>();
        foreach (var a in input.Anchors.OrderBy(a => a.Depth).ThenBy(a => a.Id, StringComparer.Ordinal))
        {
            var pos = $"anchors/{a.Id}";
            if (excluded.Contains(a.Id)) continue;
            var depthFactor = Units.DepthToMetres(a.DepthUnit);
            if (depthFactor is null)
            {
                diags.Add(new Diagnostic("unit.unconvertible",
                    $"depth unit '{a.DepthUnit}' cannot be converted; value left in place as '{a.Raw}'", pos));
                continue;
            }
            if (a.Samples.Length == 0)
            {
                diags.Add(new Diagnostic("anchor.distribution-required",
                    "anchor carries no sample distribution; a mean may not replace the distribution", pos));
                rules.Add(new RuleEvent("rule.distribution-not-mean", a.Id,
                    "anchor rejected: distribution missing; mean substitution is forbidden"));
                continue;
            }
            var ageFactor = Units.AgeToKa(a.AgeUnit);
            if (ageFactor is null)
            {
                diags.Add(new Diagnostic("unit.unconvertible",
                    $"age unit '{a.AgeUnit}' cannot be converted; value left in place as '{a.Raw}'", pos));
                continue;
            }
            var samples = a.Samples.Select(s => s * ageFactor.Value).ToArray();
            if (a.ReservoirOffset is not null)
            {
                var offFactor = a.ReservoirUnit is null ? null : Units.AgeToKa(a.ReservoirUnit);
                if (offFactor is null)
                {
                    diags.Add(new Diagnostic("anchor.reservoir-unit-unknown",
                        $"reservoir offset unit '{a.ReservoirUnit}' unknown; anchor not corrected and not used", pos));
                    continue;
                }
                var off = a.ReservoirOffset.Value * offFactor.Value;
                samples = samples.Select(s => s - off).ToArray();
            }
            Array.Sort(samples);
            var mean = samples.Average();
            var median = Percentile(samples, 50);
            if (Math.Abs(mean - median) > 0.25 * (Percentile(samples, 75) - Percentile(samples, 25) + 1e-9))
                rules.Add(new RuleEvent("rule.distribution-long-tail", a.Id,
                    $"skewed distribution: mean {mean:F2} ka differs from median {median:F2} ka; full sample set is used, never the mean"));
            anchors.Add(new NAnchor(a.Id, a.Depth * depthFactor.Value, samples, a.Raw));
        }

        var segments = new List<NSegment>();
        foreach (var s in input.Segments.OrderBy(s => s.Top).ThenBy(s => s.Id, StringComparer.Ordinal))
        {
            var f = Units.DepthToMetres(s.Unit);
            if (f is null)
            {
                diags.Add(new Diagnostic("unit.unconvertible",
                    $"segment depth unit '{s.Unit}' cannot be converted", $"segments/{s.Id}"));
                continue;
            }
            segments.Add(new NSegment(s.Id, s.Top * f.Value, s.Base * f.Value, s.Boundary, new List<NAnchor>()));
        }
        foreach (var a in anchors)
        {
            var seg = segments.FirstOrDefault(s => a.Depth >= s.Top - 1e-9 && a.Depth <= s.Base + 1e-9);
            if (seg is null)
                diags.Add(new Diagnostic("anchor.outside-segments",
                    $"anchor {a.Id} at {a.Depth:F3} m falls in a gap or outside all segments", $"anchors/{a.Id}"));
            else
                seg.Anchors.Add(a);
        }
        return new NInput(segments);
    }

    private static string InputDigest(NInput input, IReadOnlyList<string> excluded)
    {
        var segs = new JsonArray();
        foreach (var s in input.Segments)
        {
            var ans = new JsonArray();
            foreach (var a in s.Anchors.OrderBy(x => x.Depth).ThenBy(x => x.Id, StringComparer.Ordinal))
                ans.Add(new JsonObject
                {
                    ["id"] = a.Id,
                    ["depth"] = a.Depth,
                    ["samples"] = new JsonArray(a.Samples.Select(v => (JsonNode)JsonValue.Create(v)).ToArray())
                });
            segs.Add(new JsonObject { ["id"] = s.Id, ["top"] = s.Top, ["base"] = s.Base, ["boundary"] = s.Boundary, ["anchors"] = ans });
        }
        var root = new JsonObject
        {
            ["segments"] = segs,
            ["excluded"] = new JsonArray(excluded.OrderBy(x => x, StringComparer.Ordinal).Select(x => (JsonNode)JsonValue.Create(x)).ToArray())
        };
        return Canon.Digest(Canon.Serialize(root));
    }

    private static SegmentModel ComputeSegment(NSegment seg, string digest, EngineParams p, List<RuleEvent> rules)
    {
        var diags = new List<Diagnostic>();
        var ordered = seg.Anchors.OrderBy(a => a.Depth).ThenBy(a => a.Id, StringComparer.Ordinal).ToList();

        // Group anchors that share a depth (parallel anchors / depth duplicates).
        var groups = new List<List<NAnchor>>();
        foreach (var a in ordered)
        {
            if (groups.Count > 0 && Math.Abs(groups[^1][0].Depth - a.Depth) < 1e-9) groups[^1].Add(a);
            else groups.Add(new List<NAnchor> { a });
        }

        // Each conflicting depth group expands into equiprobable choices.
        var choices = new List<List<NAnchor>>();
        foreach (var g in groups)
        {
            if (g.Count == 1) { choices.Add(g); continue; }
            rules.Add(new RuleEvent("rule.parallel-anchors", seg.Id,
                $"depth {g[0].Depth:F3} m holds {g.Count} anchors: {string.Join(", ", g.Select(a => a.Id))}"));
            var mutuallyOverlap = g.All(a => g.All(b => a.SamplesKa[0] <= b.SamplesKa[^1] && b.SamplesKa[0] <= a.SamplesKa[^1]));
            if (mutuallyOverlap)
            {
                var merged = g.SelectMany(a => a.SamplesKa).OrderBy(v => v).ToArray();
                choices.Add(new List<NAnchor> { new NAnchor(string.Join("+", g.Select(a => a.Id)), g[0].Depth, merged, "merged") });
            }
            else
            {
                rules.Add(new RuleEvent("rule.parallel-anchors.conflict", seg.Id,
                    "parallel anchors have non-overlapping distributions; one equiprobable candidate per anchor is retained"));
                choices.Add(g);
            }
        }

        long candidateCount = choices.Aggregate(1L, (acc, c) => acc * c.Count);
        if (candidateCount > p.MaxCandidates)
        {
            diags.Add(new Diagnostic("budget.candidates",
                $"candidate expansion needs {candidateCount} models, budget is {p.MaxCandidates}", $"segments/{seg.Id}"));
            return new SegmentModel(seg.Id, "budget-exhausted", Array.Empty<CandidateModel>(), diags);
        }

        var combos = choices.Aggregate<IEnumerable<NAnchor[]>>(
            new[] { Array.Empty<NAnchor>() },
            (acc, c) => acc.SelectMany(prefix => c.Select(a => prefix.Append(a).ToArray()))).ToList();

        var candidates = new List<CandidateModel>();
        var rejected = 0;
        foreach (var combo in combos)
        {
            var cid = "cand-" + Canon.Digest(digest + "|" + seg.Id + "|" + string.Join(",", combo.Select(a => a.Id)))[..10];
            var medians = combo.Select(a => Percentile(a.SamplesKa, 50)).ToArray();
            var inverted = false;
            for (var i = 1; i < medians.Length; i++)
                if (medians[i] < medians[i - 1] - 1e-9) { inverted = true; break; }
            if (inverted)
            {
                rejected++;
                diags.Add(new Diagnostic("segment.inverted",
                    $"candidate {cid} violates monotonic age inside unbroken segment {seg.Id} (anchors {string.Join(",", combo.Select(a => a.Id))})",
                    $"segments/{seg.Id}"));
                continue;
            }
            candidates.Add(BuildCandidate(cid, seg, combo, digest, p));
        }

        if (candidates.Count == 0)
        {
            rules.Add(new RuleEvent("rule.monotonic-unbroken-segment", seg.Id,
                $"every candidate in segment {seg.Id} was age-inverted; the segment is semantically unsolvable"));
            return new SegmentModel(seg.Id, "inverted", Array.Empty<CandidateModel>(), diags);
        }
        if (candidates.Count > 1)
            rules.Add(new RuleEvent("rule.candidates-retained", seg.Id,
                $"{candidates.Count} equiprobable candidate models retained; none is dropped while undecidable"));
        if (rejected > 0)
            rules.Add(new RuleEvent("rule.monotonic-unbroken-segment", seg.Id,
                $"{rejected} candidate(s) rejected inside segment {seg.Id} for decreasing age with depth"));
        return new SegmentModel(seg.Id, "ok", candidates, diags);
    }

    private static CandidateModel BuildCandidate(string cid, NSegment seg, NAnchor[] combo, string digest, EngineParams p)
    {
        var rng = new Xorshift(Canon.SeedFrom(digest, (ulong)cid.GetHashCode()));
        var depths = new double[NodeCount];
        for (var i = 0; i < NodeCount; i++)
            depths[i] = seg.Top + (seg.Base - seg.Top) * i / (NodeCount - 1);

        var draws = new double[p.Draws][];
        var rateDraws = combo.Length > 1 ? new List<double>[combo.Length - 1] : Array.Empty<List<double>>();
        for (var i = 0; i < rateDraws.Length; i++) rateDraws[i] = new List<double>();

        for (var d = 0; d < p.Draws; d++)
        {
            var ages = combo.Select(a => a.SamplesKa[rng.Index(a.SamplesKa.Length)]).ToArray();
            Isotonic(ages);
            var row = new double[NodeCount];
            for (var i = 0; i < NodeCount; i++)
                row[i] = Interpolate(combo.Select(a => a.Depth).ToArray(), ages, depths[i]);
            draws[d] = row;
            for (var i = 1; i < combo.Length; i++)
            {
                var dAge = ages[i] - ages[i - 1];
                if (dAge > 1e-9)
                    rateDraws[i - 1].Add((combo[i].Depth - combo[i - 1].Depth) / dAge);
            }
        }

        var p5 = new double[NodeCount]; var p50 = new double[NodeCount]; var p95 = new double[NodeCount];
        for (var i = 0; i < NodeCount; i++)
        {
            var col = draws.Select(r => r[i]).OrderBy(v => v).ToArray();
            p5[i] = Percentile(col, 5); p50[i] = Percentile(col, 50); p95[i] = Percentile(col, 95);
        }

        var rates = new List<RateInterval>();
        for (var i = 1; i < combo.Length; i++)
        {
            var vals = rateDraws[i - 1].OrderBy(v => v).ToArray();
            if (vals.Length == 0) continue;
            rates.Add(new RateInterval(combo[i - 1].Id, combo[i].Id, combo[i - 1].Depth, combo[i].Depth,
                Percentile(vals, 5), Percentile(vals, 95), "m/ka"));
        }

        return new CandidateModel(cid, combo.Select(a => a.Id).ToArray(), true, depths, p5, p50, p95, rates);
    }

    /// <summary>Pool-adjacent-violators: enforce non-decreasing age with depth.</summary>
    private static void Isotonic(double[] y)
    {
        var n = y.Length;
        var w = new double[n]; var v = new double[n]; var c = new int[n];
        var m = 0;
        for (var i = 0; i < n; i++)
        {
            v[m] = y[i]; w[m] = 1; c[m] = i; m++;
            while (m >= 2 && v[m - 2] > v[m - 1] + 1e-12)
            {
                var nw = w[m - 2] + w[m - 1];
                v[m - 2] = (v[m - 2] * w[m - 2] + v[m - 1] * w[m - 1]) / nw;
                w[m - 2] = nw; m--;
            }
        }
        for (var b = 0; b < m; b++)
        {
            var start = b == 0 ? 0 : c[b - 1] + 1;
            for (var i = start; i <= c[b]; i++) y[i] = v[b];
        }
    }

    private static double Interpolate(double[] xs, double[] ys, double x)
    {
        if (x <= xs[0]) return ys[0];
        if (x >= xs[^1]) return ys[^1];
        for (var i = 1; i < xs.Length; i++)
        {
            if (x <= xs[i])
            {
                var t = (x - xs[i - 1]) / (xs[i] - xs[i - 1]);
                return ys[i - 1] + t * (ys[i] - ys[i - 1]);
            }
        }
        return ys[^1];
    }

    public static double Percentile(double[] sorted, double q)
    {
        if (sorted.Length == 0) return double.NaN;
        var pos = q / 100.0 * (sorted.Length - 1);
        var lo = (int)Math.Floor(pos); var hi = (int)Math.Ceiling(pos);
        if (lo == hi) return sorted[lo];
        return sorted[lo] + (sorted[hi] - sorted[lo]) * (pos - lo);
    }
}
