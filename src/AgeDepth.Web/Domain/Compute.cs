using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace AgeDepth.Domain;

/// <summary>Server-side age-depth model engine. No business rule lives only in the browser:
/// every validation, branching decision and numeric derivation happens here and is testable.</summary>
public static class AgeDepthEngine
{
    public static ModelResult Compute(
        MaterialState state,
        ModelParams p,
        IReadOnlyDictionary<string, SegmentResult>? prior = null)
    {
        var diag = new List<Diagnostic>();
        var segResults = new List<SegmentResult>();
        var reused = new List<string>();
        var boundaries = new List<ModelBoundary>();
        var priorMap = prior ?? new Dictionary<string, SegmentResult>();

        var overallRefs = new List<string>(state.MaterialRefs)
        {
            $"params:{p.Version}",
            "excluded:" + string.Join(",", state.ExcludedAnchorIds.OrderBy(x => x, StringComparer.Ordinal))
        };
        var overallDigest = Sha16(string.Join("\n", overallRefs));

        // Explicit boundaries between depth-ordered segments (gaps / unconformities).
        var ordered = state.Segments.OrderBy(s => s.TopM).ThenBy(s => s.Id, StringComparer.Ordinal).ToList();
        for (var i = 0; i < ordered.Count - 1; i++)
        {
            var a = ordered[i];
            var b = ordered[i + 1];
            var gap = Units.Fmt(b.TopM - a.BottomM);
            if (b.TopM > a.BottomM + 1e-9 && a.LowerBoundary == BoundaryKind.Gap)
                boundaries.Add(new ModelBoundary(a.Id, b.Id, BoundaryKind.Gap, a.BottomM));
            else if (b.TopM > a.BottomM + 1e-9 && a.LowerBoundary == BoundaryKind.Unconformity)
                boundaries.Add(new ModelBoundary(a.Id, b.Id, BoundaryKind.Unconformity, a.BottomM));
        }

        var overall = ComputeStatus.Ok;
        foreach (var seg in ordered)
        {
            var sr = ComputeSegment(state, seg, p);
            if (priorMap.TryGetValue(seg.Id, out var old) && old.InputDigest == sr.InputDigest && sr.Status == ComputeStatus.Ok)
            {
                segResults.Add(old);
                reused.Add(seg.Id);
            }
            else
            {
                segResults.Add(sr);
            }

            if (sr.Status is ComputeStatus.BudgetExhausted or ComputeStatus.Unsolvable
                && overall != ComputeStatus.BudgetExhausted)
                overall = sr.Status;
        }

        return new ModelResult(p.Version, overallDigest, overall, diag, segResults,
            boundaries, reused, state.ExcludedAnchorIds.ToList());
    }

    private static SegmentResult ComputeSegment(MaterialState state, CoreSegment seg, ModelParams p)
    {
        var diag = new List<Diagnostic>();
        var refs = new List<string> { state.Datum.Id, seg.Id };

        var usable = new List<AgeAnchor>();
        foreach (var anc in state.Anchors.Where(a => a.SegmentId == seg.Id).OrderBy(a => a.Id, StringComparer.Ordinal))
        {
            refs.Add(anc.Id);
            if (state.ExcludedAnchorIds.Contains(anc.Id))
            {
                diag.Add(new("anchor-excluded", anc.Id, $"anchor {anc.Id} excluded by decision; retained in store, not used"));
                continue;
            }
            if (!anc.DepthConvertible)
            {
                diag.Add(new("unconvertible-depth", anc.Id,
                    $"anchor {anc.Id} depth '{anc.RawDepth}' (unit '{anc.DepthUnit}') left in place; no default used"));
                continue;
            }
            if (anc.DepthM < seg.TopM - 1e-9 || anc.DepthM > seg.BottomM + 1e-9)
            {
                diag.Add(new("anchor-outside-segment", anc.Id,
                    $"anchor {anc.Id} depth {Units.Fmt(anc.DepthM)} m lies outside segment {seg.Id} " +
                    $"[{Units.Fmt(seg.TopM)},{Units.Fmt(seg.BottomM)}] m"));
                continue;
            }
            if (anc.Distribution.Samples.Length == 0)
            {
                diag.Add(new("empty-distribution", anc.Id, $"anchor {anc.Id} has no distribution samples; cannot model"));
                continue;
            }
            usable.Add(anc);
        }

        // Event-layer order contradictions (and an explicit overturned marker) make the
        // segment semantically unsolvable: an age curve cannot be monotonic there.
        if (seg.IsInverted || DetectInversion(state, seg))
        {
            diag.Add(new("inverted-segment", seg.Id,
                $"segment {seg.Id} is overturned ({seg.InversionEvidence}); age is not monotonic in depth"));
            return new SegmentResult(seg.Id, Digest(seg, refs, p, state), ComputeStatus.Unsolvable, diag, Array.Empty<Candidate>());
        }

        if (usable.Count == 0)
        {
            diag.Add(new("no-anchors", seg.Id, $"segment {seg.Id} has no usable age anchor; input incomplete"));
            return new SegmentResult(seg.Id, Digest(seg, refs, p, state), ComputeStatus.IncompleteInput, diag, Array.Empty<Candidate>());
        }

        // Parallel / duplicate-depth groups are retained and branch the candidate set.
        var groups = usable
            .GroupBy(a => Units.Fmt(a.DepthM))
            .OrderBy(g => g.Key)
            .Select(g => g.OrderBy(a => a.Id, StringComparer.Ordinal).ToList())
            .ToList();

        foreach (var g in groups.Where(g => g.Count > 1))
            diag.Add(new("parallel-anchors", seg.Id,
                $"depth {g.Key:0.######} m is shared by {g.Count} anchors: {string.Join(", ", g.Select(a => a.Id))}; " +
                "all alternatives retained as candidate branches"));

        var combos = Cartesian(groups);
        if (combos.Count > p.MaxCandidates)
        {
            diag.Add(new("budget-exhausted", seg.Id,
                $"segment {seg.Id} needs {combos.Count} candidates but budget is {p.MaxCandidates}"));
            return new SegmentResult(seg.Id, Digest(seg, refs, p, state), ComputeStatus.BudgetExhausted, diag, Array.Empty<Candidate>());
        }

        var candidates = new List<Candidate>();
        foreach (var combo in combos)
        {
            var orderedAnc = combo.OrderBy(a => a.DepthM).ThenBy(a => a.Id, StringComparer.Ordinal).ToList();
            var nonMono = new List<string>();
            for (var i = 1; i < orderedAnc.Count; i++)
            {
                // Monotonic only inside this unbroken segment. Age (BP) must not decrease with depth;
                // the full distribution is checked via quantile overlap, never via a mean substitute.
                if (orderedAnc[i].Distribution.Quantile(p.HiQ) < orderedAnc[i - 1].Distribution.Quantile(p.LoQ) - 1e-9)
                    nonMono.Add($"{orderedAnc[i - 1].Id}->{orderedAnc[i].Id}");
            }
            if (nonMono.Count > 0)
            {
                diag.Add(new("non-monotonic", seg.Id,
                    $"candidate {Ids(orderedAnc)} violates depth-ordered age at {string.Join("; ", nonMono)}"));
                continue;
            }

            var points = new List<AnchorPoint>();
            var rates = new List<RateInterval>();
            foreach (var a in orderedAnc)
            {
                var med = a.Distribution.Quantile(0.5) - a.ReservoirOffsetKa;
                points.Add(new AnchorPoint(a.Id, a.DepthM, med,
                    a.Distribution.Quantile(p.LoQ) - a.ReservoirOffsetKa,
                    a.Distribution.Quantile(p.HiQ) - a.ReservoirOffsetKa));
            }
            for (var i = 1; i < points.Count; i++)
            {
                var dSpan = Units.Fmt(points[i].DepthM - points[i - 1].DepthM);
                if (dSpan <= 0 || points[i].Lo - points[i - 1].Hi <= 1e-12)
                {
                    diag.Add(new("zero-rate-interval", seg.Id,
                        $"interval {points[i - 1].AnchorId}->{points[i].AnchorId} has no positive age span"));
                    rates.Add(new RateInterval(points[i - 1].AnchorId, points[i].AnchorId,
                        points[i - 1].DepthM, points[i].DepthM, double.NaN, double.NaN, "m/ka"));
                }
                else
                {
                    var rateHi = dSpan / (points[i].Lo - points[i - 1].Hi);
                    var rateLo = dSpan / (points[i].Hi - points[i - 1].Lo);
                    rates.Add(new RateInterval(points[i - 1].AnchorId, points[i].AnchorId,
                        points[i - 1].DepthM, points[i].DepthM, Units.Fmt(rateLo), Units.Fmt(rateHi), "m/ka"));
                }
            }

            var id = "cand-" + Sha16(seg.Id + "|" + string.Join(",", points.Select(x => x.AnchorId)));
            candidates.Add(new Candidate(id, 0, orderedAnc.Select(x => x.Id).ToList(), points, rates));
        }

        // Equivalent inputs must produce an equivalent, stable order.
        candidates = candidates.OrderBy(c => c.Id, StringComparer.Ordinal).ToList();
        candidates = candidates.Select((c, i) => c with { Rank = i }).ToList();

        if (candidates.Count == 0)
            return new SegmentResult(seg.Id, Digest(seg, refs, p, state), ComputeStatus.Unsolvable, diag, Array.Empty<Candidate>());

        // Multiple equiprobable candidates are retained.
        return new SegmentResult(seg.Id, Digest(seg, refs, p, state), ComputeStatus.Ok, diag, candidates);
    }

    private static bool DetectInversion(MaterialState state, CoreSegment seg)
    {
        var layers = state.EventLayers.Where(e => e.SegmentId == seg.Id).ToList();
        foreach (var e in layers)
        {
            if (e.OlderThanLayerId is null) continue;
            var older = layers.FirstOrDefault(l => l.Id == e.OlderThanLayerId);
            if (older is null) continue;
            // e claims to be older than `older`; older must therefore sit deeper.
            if (older.DepthM < e.DepthM - 1e-9) return true;
        }
        return false;
    }

    private static string Ids(List<AgeAnchor> anchors) => "[" + string.Join(">", anchors.Select(a => a.Id)) + "]";

    private static List<List<AgeAnchor>> Cartesian(List<List<AgeAnchor>> groups)
    {
        var result = new List<List<AgeAnchor>> { new() };
        foreach (var g in groups)
        {
            var next = new List<List<AgeAnchor>>(result.Count * g.Count);
            foreach (var prefix in result)
            foreach (var choice in g)
                next.Add(prefix.Concat(new[] { choice }).ToList());
            result = next;
        }
        return result;
    }

    private static string Digest(CoreSegment seg, List<string> refs, ModelParams p, MaterialState state)
    {
        refs.Add($"params:{p.Version}");
        refs.Add("excluded:" + string.Join(",", state.ExcludedAnchorIds.OrderBy(x => x, StringComparer.Ordinal)));
        refs.Add("datum:" + state.Datum.VerticalCrs);
        return Sha16(string.Join("\n", refs));
    }

    public static string Sha16(string s)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(s));
        return Convert.ToHexString(bytes)[..16].ToLowerInvariant();
    }

    public static string CanonicalJson(object o)
    {
        return JsonSerializer.Serialize(o, new JsonSerializerOptions
        {
            WriteIndented = false,
            PropertyNamingPolicy = null,
            DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.Never,
        });
    }
}
