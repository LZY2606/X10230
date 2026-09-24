using System.Security.Cryptography;
using System.Text;
using AgeDepth.Domain;

namespace AgeDepth.Engine;

public static partial class AgeDepthEngine
{
    private static SegmentModel BuildSegment(Segment seg, List<AgeControl> controls,
        ModelParameters p, List<Diagnostic> diag)
    {
        var segInputHash = HashSegmentInput(seg, controls, p);
        if (seg.IsInverted)
        {
            diag.Add(new Diagnostic { Code = "INVERTED_SEGMENT", Severity = "error",
                Message = $"segment '{seg.Id}' is mapped as an inverted section; ages are not forced to monotonic order",
                Position = $"$.segments[{seg.Id}].isInverted", ObjectId = seg.Id });
            return new SegmentModel { SegmentId = seg.Id, Solved = false, InputHash = segInputHash };
        }

        var mine = controls
            .Where(c => c.SegmentId == seg.Id)
            .OrderBy(c => c.Depth)
            .ThenBy(c => c.Id, StringComparer.Ordinal)
            .ToList();

        var depths = mine.Select(c => c.Depth).Distinct().OrderBy(d => d).ToList();
        var excluded = mine.Where(c => c.Excluded).ToList();
        var active = mine.Where(c => !c.Excluded).ToList();
        foreach (var ex in excluded)
            diag.Add(new Diagnostic { Code = "ANCHOR_EXCLUDED", Severity = "info",
                Message = $"{ex.Kind} '{ex.Id}' is excluded by a recorded decision; its distribution is retained but unused",
                Position = $"$.segments[{seg.Id}]", ObjectId = ex.Id });

        if (active.Count == 0)
        {
            diag.Add(new Diagnostic { Code = "NO_ACTIVE_CONTROL", Severity = "error",
                Message = $"segment '{seg.Id}' has no active age control",
                Position = $"$.segments[{seg.Id}]", ObjectId = seg.Id });
            return new SegmentModel { SegmentId = seg.Id, Solved = false, InputHash = segInputHash };
        }

        var activeDepths = active.Select(c => c.Depth).Distinct().OrderBy(d => d).ToList();
        if (activeDepths.Count < 2)
        {
            diag.Add(new Diagnostic { Code = "UNDERCONSTRAINED_SEGMENT", Severity = "warning",
                Message = $"segment '{seg.Id}' has age control at one depth only; rates cannot be derived",
                Position = $"$.segments[{seg.Id}]", ObjectId = seg.Id });
        }

        // Deterministic feasibility pre-check using distribution extrema. This separates
        // the semantic "no solution exists" case from the resource-budget case.
        for (var i = 1; i < activeDepths.Count; i++)
        {
            var upper = active.Where(c => Math.Abs(c.Depth - activeDepths[i - 1]) < 1e-9).ToList();
            var lower = active.Where(c => c.Depth - activeDepths[i] > -1e-9 && Math.Abs(c.Depth - activeDepths[i]) < 1e-9).ToList();
            var upperMax = upper.Max(c => EffectiveMax(c));
            var lowerMin = lower.Min(c => EffectiveMin(c));
            if (lowerMin <= upperMax)
            {
                diag.Add(new Diagnostic
                {
                    Code = "NON_MONOTONIC_UNSOLVABLE", Severity = "error",
                    Message = $"segment '{seg.Id}': depths {activeDepths[i - 1]:0.###} and {activeDepths[i]:0.###} m "
                        + "have disjoint/overlapping distributions so age cannot increase with depth in this continuous section",
                    Position = $"$.segments[{seg.Id}]", ObjectId = seg.Id
                });
                return new SegmentModel { SegmentId = seg.Id, Solved = false, InputHash = segInputHash };
            }
        }
        // Multiple anchors sharing one depth (并列锚点): their distributions must overlap.
        foreach (var d in activeDepths)
        {
            var group = active.Where(c => Math.Abs(c.Depth - d) < 1e-9).ToList();
            if (group.Count > 1 && !DistributionsOverlap(group))
            {
                diag.Add(new Diagnostic
                {
                    Code = "PARALLEL_ANCHORS_DISJOINT", Severity = "error",
                    Message = $"segment '{seg.Id}': {group.Count} co-located anchors at {d:0.###} m have disjoint distributions; no consistent age exists",
                    Position = $"$.segments[{seg.Id}]",
                    ObjectId = string.Join("|", group.Select(c => c.Id))
                });
                return new SegmentModel { SegmentId = seg.Id, Solved = false, InputHash = segInputHash };
            }
            if (group.Count > 1)
                diag.Add(new Diagnostic { Code = "PARALLEL_ANCHORS", Severity = "info",
                    Message = $"{group.Count} parallel anchors at {d:0.###} m are sampled jointly",
                    Position = $"$.segments[{seg.Id}]", ObjectId = seg.Id });
        }
        if (mine.Count > active.Count || depths.Count != mine.Count)
            diag.EnsureCapacity(diag.Count);

        var candidates = new List<CandidateModel>();
        var rng = new DeterministicRng(p.Seed ^ StableHashInt(segInputHash));
        var attempts = 0;
        var budgetHit = false;
        while (candidates.Count < p.EnsembleSize)
        {
            attempts++;
            if (attempts > p.MaxIterations) { budgetHit = true; break; }

            var draw = DrawDepthGroups(active, activeDepths, rng);
            if (draw is null) continue;

            var nodes = draw!;
            var ages = nodes.Select(n => n.Age).ToList();
            if (!IsStrictlyIncreasing(ages)) continue;

            var rates = new List<double>();
            for (var i = 1; i < nodes.Count; i++)
            {
                var dd = nodes[i].Depth - nodes[i - 1].Depth;
                rates.Add((nodes[i].Age - nodes[i - 1].Age) / dd);
            }
            var roughness = 0.0;
            for (var i = 1; i < rates.Count; i++)
                roughness += Math.Pow(rates[i] - rates[i - 1], 2);

            candidates.Add(new CandidateModel
            {
                CandidateId = $"{seg.Id}#c{candidates.Count:D4}",
                DrawIndex = attempts - 1,
                Roughness = roughness,
                CoOptimal = false,
                Nodes = nodes.Select(n => (n.Depth, n.Age)).ToList(),
                Rates = rates
            });
        }

        if (candidates.Count == 0)
        {
            diag.Add(new Diagnostic { Code = "BUDGET_EXHAUSTED", Severity = "error",
                Message = $"segment '{seg.Id}': monotonic ensemble could not be built within {p.MaxIterations} iterations",
                Position = $"$.segments[{seg.Id}]", ObjectId = seg.Id });
            return new SegmentModel { SegmentId = seg.Id, Solved = false, InputHash = segInputHash };
        }
        if (budgetHit)
            diag.Add(new Diagnostic { Code = "BUDGET_PARTIAL", Severity = "warning",
                Message = $"segment '{seg.Id}': budget reached with {candidates.Count}/{p.EnsembleSize} candidates",
                Position = $"$.segments[{seg.Id}]", ObjectId = seg.Id });

        var best = candidates.Min(c => c.Roughness);
        var coOptimalIds = candidates
            .Where(c => c.Roughness <= best * p.EqualTolerance + 1e-12)
            .OrderBy(c => c.Roughness)
            .ThenBy(c => c.CandidateId, StringComparer.Ordinal)
            .Select(c => c.CandidateId)
            .ToHashSet();
        candidates = candidates.Select(c => c with { CoOptimal = coOptimalIds.Contains(c.CandidateId) }).ToList();

        var bands = BuildBands(nodesDepth: depths, mine: mine, active: active,
            activeDepths: activeDepths, candidates: candidates, p: p);
        var rateIntervals = BuildRates(candidates);
        return new SegmentModel
        {
            SegmentId = seg.Id,
            Solved = true,
            Band = bands.Band,
            Rates = rateIntervals,
            Candidates = candidates,
            CoOptimalCandidates = candidates.Where(c => c.CoOptimal).ToList(),
            InputHash = segInputHash
        };
    }

    private static double EffectiveMin(AgeControl c)
        => c.AgeSamples[0] - (c.ReservoirSamples is null ? 0 : c.ReservoirSamples[^1]);

    private static double EffectiveMax(AgeControl c)
        => c.AgeSamples[^1] - (c.ReservoirSamples is null ? 0 : c.ReservoirSamples[0]);

    private static bool DistributionsOverlap(List<AgeControl> group)
    {
        var lo = group.Max(c => EffectiveMin(c));
        var hi = group.Min(c => EffectiveMax(c));
        return hi >= lo;
    }

    private static List<DrawNode>? DrawDepthGroups(List<AgeControl> active, List<double> activeDepths,
        DeterministicRng rng)
    {
        var nodes = new List<DrawNode>();
        foreach (var d in activeDepths)
        {
            var group = active.Where(c => Math.Abs(c.Depth - d) < 1e-9).ToList();
            var ages = new List<double>();
            foreach (var c in group)
            {
                var a = SampleFrom(c.AgeSamples, rng);
                if (c.ReservoirSamples is not null) a -= SampleFrom(c.ReservoirSamples, rng);
                ages.Add(a);
            }
            nodes.Add(new DrawNode(d, ages.Min(), ages.Max(), group.Select(c => c.Id).OrderBy(x => x).ToArray()));
        }
        return nodes;
    }

    private static double SampleFrom(IReadOnlyList<double> sorted, DeterministicRng rng)
    {
        var u = rng.NextDouble();
        var pos = u * (sorted.Count - 1);
        var lo = (int)Math.Floor(pos);
        var hi = Math.Min(lo + 1, sorted.Count - 1);
        return sorted[lo] + (sorted[hi] - sorted[lo]) * (pos - lo);
    }

    private static bool IsStrictlyIncreasing(List<double> xs)
    {
        for (var i = 1; i < xs.Count; i++)
            if (xs[i] <= xs[i - 1]) return false;
        return true;
    }

    private static List<RateInterval> BuildRates(List<CandidateModel> candidates)
    {
        var intervals = new List<RateInterval>();
        var nodes = candidates[0].Nodes;
        for (var i = 1; i < nodes.Count; i++)
        {
            var rates = candidates.Select(c => c.Rates[i - 1]).OrderBy(x => x).ToList();
            intervals.Add(new RateInterval
            {
                DepthTop = nodes[i - 1].Depth,
                DepthBottom = nodes[i].Depth,
                RateP025 = AgeAnchor.Percentile(rates, 0.025),
                RateP500 = AgeAnchor.Percentile(rates, 0.5),
                RateP975 = AgeAnchor.Percentile(rates, 0.975)
            });
        }
        return intervals;
    }

    private static (List<BandNode> Band) BuildBands(List<double> nodesDepth, List<AgeControl> mine,
        List<AgeControl> active, List<double> activeDepths, List<CandidateModel> candidates,
        ModelParameters p)
    {
        var band = new List<BandNode>();
        var evalDepths = activeDepths;
        foreach (var d in evalDepths)
        {
            var ages = new List<double>();
            foreach (var cand in candidates)
                ages.Add(Interpolate(cand.Nodes, d));
            ages.Sort();
            var ids = active.Where(c => Math.Abs(c.Depth - d) < 1e-9)
                .Select(c => c.Id).OrderBy(x => x, StringComparer.Ordinal).ToList();
            band.Add(new BandNode
            {
                Depth = d,
                AgeP025 = AgeAnchor.Percentile(ages, p.BandLower),
                AgeP500 = AgeAnchor.Percentile(ages, 0.5),
                AgeP975 = AgeAnchor.Percentile(ages, p.BandUpper),
                AnchorIds = ids
            });
        }
        return (band);
    }

    private static double Interpolate(IReadOnlyList<(double Depth, double Age)> nodes, double depth)
    {
        if (depth <= nodes[0].Depth) return nodes[0].Age;
        if (depth >= nodes[^1].Depth) return nodes[^1].Age;
        for (var i = 1; i < nodes.Count; i++)
        {
            if (depth <= nodes[i].Depth)
            {
                var t = (depth - nodes[i - 1].Depth) / (nodes[i].Depth - nodes[i - 1].Depth);
                return nodes[i - 1].Age + t * (nodes[i].Age - nodes[i - 1].Age);
            }
        }
        return nodes[^1].Age;
    }

    private static string HashSegmentInput(Segment seg, List<AgeControl> controls, ModelParameters p)
    {
        var canonical = System.Text.Json.JsonSerializer.Serialize(new
        {
            seg = new { seg.Id, top = seg.TopDepth.Value, bottom = seg.BottomDepth.Value, seg.IsInverted },
            controls = controls.Where(c => c.SegmentId == seg.Id)
                .OrderBy(c => c.Depth).ThenBy(c => c.Id, StringComparer.Ordinal)
                .Select(c => new { c.Id, c.Depth, c.AgeSamples, reservoir = c.ReservoirSamples, c.Excluded }),
            p
        });
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(canonical)))[..16];
    }

    private static int StableHashInt(string hex)
    {
        var h = 0;
        foreach (var ch in hex) h = (h * 31 + ch) & 0x7fffffff;
        return h;
    }

    private sealed record DrawNode(double Depth, double AgeLo, double AgeHi, string[] Ids)
    {
        public double Age => (AgeLo + AgeHi) / 2.0;
    }
}
