using System.Security.Cryptography;
using System.Text;

namespace AgeModel.Domain;

public enum ComputationStatus { Ok, BudgetExhausted, FormatCorrupt, SemanticUnsolvable }

public sealed record TiePoint(string AnchorId, double Depth, double MedianAge, double LowerAge, double UpperAge);

public sealed record Candidate(string CandidateId, IReadOnlyList<TiePoint> TiePoints, double FitScore)
{
    /// <summary>Age band at a depth inside the segment, interpolated between quantile curves.
    /// Never uses a mean: lower/median/upper follow the anchor distributions.</summary>
    public (double median, double lower, double upper) BandAt(double depth)
    {
        var t = TiePoints;
        if (t.Count == 0) return (double.NaN, double.NaN, double.NaN);
        if (depth <= t[0].Depth) return (t[0].MedianAge, t[0].LowerAge, t[0].UpperAge);
        if (depth >= t[^1].Depth) return (t[^1].MedianAge, t[^1].LowerAge, t[^1].UpperAge);
        for (var i = 1; i < t.Count; i++)
        {
            if (depth <= t[i].Depth)
            {
                var f = (depth - t[i - 1].Depth) / (t[i].Depth - t[i - 1].Depth);
                return (Lerp(t[i - 1].MedianAge, t[i].MedianAge, f),
                        Lerp(t[i - 1].LowerAge, t[i].LowerAge, f),
                        Lerp(t[i - 1].UpperAge, t[i].UpperAge, f));
            }
        }
        return (t[^1].MedianAge, t[^1].LowerAge, t[^1].UpperAge);
        static double Lerp(double a, double b, double f) => a + (b - a) * f;
    }

    /// <summary>Sedimentation rate intervals (m/ka) between consecutive tie points.
    /// Invalid intervals keep their position and carry a reason instead of a number.</summary>
    public IReadOnlyList<RateInterval> Rates()
    {
        var list = new List<RateInterval>();
        for (var i = 1; i < TiePoints.Count; i++)
        {
            var a = TiePoints[i - 1]; var b = TiePoints[i];
            var dd = b.Depth - a.Depth;
            var da = b.MedianAge - a.MedianAge;
            if (da <= 0)
                list.Add(new RateInterval(a.AnchorId, b.AnchorId, a.Depth, b.Depth, null, null, null,
                    $"non-positive age span {da:F3} ka: rate undefined, interval kept with reason"));
            else
                list.Add(new RateInterval(a.AnchorId, b.AnchorId, a.Depth, b.Depth,
                    dd / da,
                    dd / (b.UpperAge - a.LowerAge),
                    dd / (b.LowerAge - a.UpperAge > 0 ? b.LowerAge - a.UpperAge : 1e-9),
                    null));
        }
        return list;
    }
}

public sealed record RateInterval(string FromAnchorId, string ToAnchorId, double TopDepth, double BottomDepth,
    double? MedianMPerKa, double? SlowMPerKa, double? FastMPerKa, string? InvalidReason);

public sealed record SegmentResult(
    string SegmentId, double TopDepth, double BottomDepth,
    IReadOnlyList<string> AnchorIds,
    IReadOnlyList<Candidate> Candidates,
    IReadOnlyList<Judgement> Judgements,
    string InputHash);

public sealed record ComputationResult(
    ComputationStatus Status,
    IReadOnlyList<SegmentResult> Segments,
    IReadOnlyList<Judgement> Judgements,
    string? Failure)
{
    public IEnumerable<Candidate> AllCandidates() => Segments.SelectMany(s => s.Candidates);
}

public static class Engine
{
    /// <summary>Full deterministic computation. Equivalent inputs always produce
    /// the same segment/candidate ordering and the same hashes.</summary>
    public static ComputationResult Compute(IReadOnlyList<Material> materials, Parameters p)
        => ComputeIncremental(materials, p, null).Result;

    /// <summary>Incremental computation: segments whose input hash is unchanged keep their
    /// previous result object (local recompute); only affected segments are rebuilt.</summary>
    public static (ComputationResult Result, IReadOnlyList<string> RecomputedSegmentIds) ComputeIncremental(
        IReadOnlyList<Material> materials, Parameters p, ComputationResult? previous)
    {
        var judgements = new List<Judgement>();
        var formatIssues = new List<string>();

        var excluded = materials.OfType<ExclusionDecisionMaterial>()
            .Select(e => e.TargetMaterialId).ToHashSet(StringComparer.Ordinal);
        foreach (var e in materials.OfType<ExclusionDecisionMaterial>())
            judgements.Add(new Judgement("exclusion-recorded", null, null,
                $"{e.Id}: '{e.TargetMaterialId}' excluded by {e.DecidedBy}: {e.Reason}"));

        var anchors = materials.OfType<RadiometricAgeMaterial>()
            .Where(a => !excluded.Contains(a.Id)).ToList();
        var boundaries = materials.OfType<BoundaryMaterial>()
            .Where(b => !excluded.Contains(b.Id)).ToList();

        foreach (var m in materials)
        {
            var q = m switch
            {
                RadiometricAgeMaterial r => r.Depth,
                BoundaryMaterial b => b.Depth,
                EventLayerMaterial ev => ev.Depth,
                _ => null
            };
            if (q?.Diagnostic is { } diag)
                judgements.Add(new Judgement(Judgement.UnconvertibleValue, null, null, $"{m.Id}: {diag}"));
        }

        foreach (var a in anchors)
        {
            if (a.Depth.Value is null)
                formatIssues.Add($"{a.Id}: depth '{a.Depth.Raw}' is not convertible");
            if (a.Age.Quantiles.Count == 0)
                formatIssues.Add($"{a.Id}: age distribution has no quantiles");
            else
            {
                for (var i = 1; i < a.Age.Quantiles.Count; i++)
                    if (a.Age.Quantiles[i].P <= a.Age.Quantiles[i - 1].P)
                        formatIssues.Add($"{a.Id}: quantile probabilities must be strictly increasing");
            }
        }
        if (formatIssues.Count > 0)
            return (new ComputationResult(ComputationStatus.FormatCorrupt, Array.Empty<SegmentResult>(),
                judgements, string.Join("; ", formatIssues)), Array.Empty<string>());

        var usableAnchors = anchors.Where(a => a.Depth.Value is not null).ToList();
        var boundaryDepths = boundaries
            .Where(b => b.Depth.Value is not null)
            .OrderBy(b => b.Depth.Value!.Value).ThenBy(b => b.Id, StringComparer.Ordinal)
            .ToList();

        foreach (var b in boundaryDepths)
            judgements.Add(new Judgement(Judgement.ExplicitBoundary, null, b.Depth.Value,
                $"{b.Id}: {b.BoundaryKind} at {b.Depth.Raw} {b.Depth.Unit} ({b.Datum}): {b.Reason}"));

        // Build continuous segments between explicit boundaries.
        var cuts = boundaryDepths.Select(b => b.Depth.Value!.Value).Distinct().OrderBy(x => x).ToList();
        var edges = new List<double> { double.NegativeInfinity };
        edges.AddRange(cuts);
        edges.Add(double.PositiveInfinity);

        var segments = new List<SegmentResult>();
        var recomputed = new List<string>();
        var previousByHash = previous?.Segments.GroupBy(s => s.InputHash)
            .ToDictionary(g => g.Key, g => g.First()) ?? new Dictionary<string, SegmentResult>();

        for (var i = 0; i < edges.Count - 1; i++)
        {
            var top = edges[i]; var bottom = edges[i + 1];
            var inSegment = usableAnchors
                .Where(a => a.Depth.Value!.Value > top && a.Depth.Value!.Value < bottom)
                .OrderBy(a => a.Depth.Value!.Value).ThenBy(a => a.Id, StringComparer.Ordinal)
                .ToList();
            var segId = $"seg-{i:D2}";
            var hash = SegmentHash(segId, inSegment, p);
            if (previousByHash.TryGetValue(hash, out var reused))
            {
                segments.Add(reused with { SegmentId = segId });
                continue;
            }
            recomputed.Add(segId);
            var built = BuildSegment(segId, top, bottom, inSegment, p, judgements, out var status, out var failure);
            if (status is ComputationStatus.BudgetExhausted or ComputationStatus.FormatCorrupt)
                return (new ComputationResult(status, segments, judgements, failure), recomputed);
            segments.Add(built);
        }

        var anyAnchor = usableAnchors.Count > 0;
        var anyCandidate = segments.Any(s => s.Candidates.Count > 0);
        if (anyAnchor && !anyCandidate)
            return (new ComputationResult(ComputationStatus.SemanticUnsolvable, segments, judgements,
                "no continuous segment admits a monotonic age model"), recomputed);

        if (recomputed.Count > 0 && previous is not null)
            judgements.Add(new Judgement(Judgement.LocalRecompute, null, null,
                $"recomputed segments: {string.Join(",", recomputed)}; reused: {segments.Count - recomputed.Count}"));

        return (new ComputationResult(ComputationStatus.Ok, segments, judgements, null), recomputed);
    }

    private static SegmentResult BuildSegment(
        string segId, double top, double bottom, List<RadiometricAgeMaterial> anchors,
        Parameters p, List<Judgement> judgements, out ComputationStatus status, out string? failure)
    {
        status = ComputationStatus.Ok;
        failure = null;
        var segJudgements = new List<Judgement>();
        var topV = double.IsNegativeInfinity(top) ? (anchors.FirstOrDefault()?.Depth.Value ?? 0) : top;
        var bottomV = double.IsPositiveInfinity(bottom) ? (anchors.LastOrDefault()?.Depth.Value ?? 0) : bottom;

        foreach (var a in anchors)
        {
            var dist = a.CorrectedAge();
            if (Math.Abs(dist.Skew) > p.LongTailSkewKa)
                segJudgements.Add(new Judgement(Judgement.LongTailDistribution, segId, a.Depth.Value,
                    $"{a.Id}: long-tail distribution skew {dist.Skew:F3} ka kept as full quantile set"));
            segJudgements.Add(new Judgement(Judgement.DistributionKept, segId, a.Depth.Value,
                $"{a.Id}: {dist.Quantiles.Count} quantiles used directly, mean never substituted"));
        }

        // Group by exact depth: depth duplicates become parallel anchors.
        var groups = anchors.GroupBy(a => a.Depth.Value!.Value)
            .OrderBy(g => g.Key)
            .Select(g => g.OrderBy(a => a.Id, StringComparer.Ordinal).ToList())
            .ToList();
        foreach (var g in groups.Where(g => g.Count > 1))
        {
            segJudgements.Add(new Judgement(Judgement.DepthDuplicate, segId, g[0].Depth.Value,
                $"{g.Count} anchors share depth {g[0].Depth.Raw} {g[0].Depth.Unit}: {string.Join(",", g.Select(x => x.Id))}"));
            segJudgements.Add(new Judgement(Judgement.ParallelAnchors, segId, g[0].Depth.Value,
                "parallel anchors kept; each representative choice branches a candidate"));
        }

        // Candidate space: one representative per depth group.
        long combinations = 1;
        foreach (var g in groups) combinations *= g.Count;
        if (combinations > p.MaxCombinations)
        {
            status = ComputationStatus.BudgetExhausted;
            failure = $"segment {segId}: {combinations} anchor combinations exceed budget {p.MaxCombinations}";
            return new SegmentResult(segId, topV, bottomV, anchors.Select(a => a.Id).ToList(),
                Array.Empty<Candidate>(), segJudgements, "");
        }

        var candidates = new List<Candidate>();
        foreach (var choice in Cartesian(groups))
        {
            var ties = choice.Select(a =>
            {
                var d = a.CorrectedAge();
                return new TiePoint(a.Id, a.Depth.Value!.Value, d.Median, d.Lower, d.Upper);
            }).ToList();
            var monotonic = true;
            for (var i = 1; i < ties.Count; i++)
                if (ties[i].MedianAge < ties[i - 1].MedianAge - p.EqualFitToleranceKa)
                { monotonic = false; break; }
            if (!monotonic) continue;
            var id = segId + ":" + string.Join("+", choice.Select(c => c.Id));
            candidates.Add(new Candidate(id, ties, Smoothness(ties)));
        }

        if (groups.Count > 0 && candidates.Count == 0)
        {
            segJudgements.Add(new Judgement(Judgement.InvertedSection, segId, null,
                $"segment {segId}: every anchor combination violates monotonic age-with-depth; " +
                "section flagged as inverted/unsolvable, no silent repair applied"));
        }
        else if (candidates.Count > 0)
        {
            segJudgements.Add(new Judgement(Judgement.MonotonicEnforced, segId, null,
                $"segment {segId}: age increases with depth inside continuous section only"));
            // Keep every candidate within tolerance of the best fit: equally defensible models.
            var best = candidates.Min(c => c.FitScore);
            var kept = candidates.Where(c => c.FitScore <= best + p.EqualFitToleranceKa)
                .OrderBy(c => c.CandidateId, StringComparer.Ordinal).ToList();
            if (kept.Count > 1)
                segJudgements.Add(new Judgement(Judgement.EqualCandidatesKept, segId, null,
                    $"{kept.Count} equally defensible candidates retained (fit within {p.EqualFitToleranceKa} ka)"));
            candidates = kept;
        }

        return new SegmentResult(segId, topV, bottomV, anchors.Select(a => a.Id).ToList(),
            candidates, segJudgements, "");
    }

    private static double Smoothness(IReadOnlyList<TiePoint> ties)
    {
        double score = 0;
        for (var i = 2; i < ties.Count; i++)
        {
            var r1 = (ties[i - 1].Depth - ties[i - 2].Depth) / Math.Max(1e-9, ties[i - 1].MedianAge - ties[i - 2].MedianAge);
            var r2 = (ties[i].Depth - ties[i - 1].Depth) / Math.Max(1e-9, ties[i].MedianAge - ties[i - 1].MedianAge);
            score += Math.Abs(r2 - r1);
        }
        return score;
    }

    private static IEnumerable<List<RadiometricAgeMaterial>> Cartesian(List<List<RadiometricAgeMaterial>> groups)
    {
        IEnumerable<List<RadiometricAgeMaterial>> acc = new List<List<RadiometricAgeMaterial>> { new() };
        foreach (var g in groups)
            acc = acc.SelectMany(prefix => g.Select(a => new List<RadiometricAgeMaterial>(prefix) { a }));
        return acc;
    }

    public static string SegmentHash(string segId, IReadOnlyList<RadiometricAgeMaterial> anchors, Parameters p)
    {
        var sb = new StringBuilder(segId).Append('|').Append(p.Version).Append('|')
            .Append(p.MaxCombinations).Append('|').Append(p.EqualFitToleranceKa);
        foreach (var a in anchors.OrderBy(a => a.Id, StringComparer.Ordinal))
        {
            sb.Append('|').Append(a.Id).Append(':').Append(a.Depth.Raw).Append(':').Append(a.Depth.Unit)
              .Append(':').Append(a.Depth.Datum).Append(':')
              .Append(string.Join(",", a.Age.Quantiles.Select(q => $"{q.P}={q.Age}")))
              .Append(':').Append(a.ReservoirOffsetYears?.Raw ?? "-");
        }
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(sb.ToString())))[..16];
    }
}
