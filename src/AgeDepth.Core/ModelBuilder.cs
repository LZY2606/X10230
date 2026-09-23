using System.Text;

namespace AgeDepth.Core;

/// <summary>
/// Builds age-depth model runs. Rules enforced here (server side, testable):
///  - age may only be monotonic inside a continuous segment;
///  - depth gaps and unconformities are explicit boundaries, never interpolated across;
///  - an anchor's distribution is sampled, never replaced by its mean;
///  - multiple equally-defensible candidates are all retained.
/// </summary>
public static class ModelBuilder
{
    private const double DepthEpsilon = 1e-6;

    public static IReadOnlyList<Segment> BuildSegments(
        IReadOnlyList<Section> sections,
        IReadOnlyList<Boundary> boundaries,
        IReadOnlyList<AgeAnchor> anchors,
        List<Diagnostic> diagnostics)
    {
        var segments = new List<Segment>();
        var orderedBoundaries = boundaries.OrderBy(b => b.Depth).ThenBy(b => b.Id, StringComparer.Ordinal).ToList();
        var orderedSections = sections.OrderBy(s => s.TopDepth).ThenBy(s => s.Id, StringComparer.Ordinal).ToList();

        foreach (var section in orderedSections)
        {
            if (section.TopDepth > section.BottomDepth)
            {
                diagnostics.Add(new Diagnostic(
                    "section.inverted",
                    $"Section {section.Id} has top {section.TopDepth} below bottom {section.BottomDepth}; section skipped.",
                    $"section:{section.Id}"));
                continue;
            }
            var cuts = orderedBoundaries
                .Where(b => b.Depth > section.TopDepth + DepthEpsilon && b.Depth < section.BottomDepth - DepthEpsilon)
                .Select(b => b.Depth).ToList();
            var edges = new List<double> { section.TopDepth };
            edges.AddRange(cuts);
            edges.Add(section.BottomDepth);
            for (var i = 0; i < edges.Count - 1; i++)
            {
                var top = edges[i];
                var bottom = edges[i + 1];
                if (bottom - top < DepthEpsilon) continue;
                var topBoundary = orderedBoundaries.FirstOrDefault(b => Math.Abs(b.Depth - top) < 1e-4);
                var bottomBoundary = orderedBoundaries.FirstOrDefault(b => Math.Abs(b.Depth - bottom) < 1e-4);
                var id = $"seg:{section.CoreId}:{top:F3}:{bottom:F3}";
                segments.Add(new Segment(id, section.CoreId, top, bottom, topBoundary?.Id, bottomBoundary?.Id,
                    new List<AgeAnchor>()));
            }
        }

        var assigned = new HashSet<string>(StringComparer.Ordinal);
        foreach (var anchor in anchors.OrderBy(a => a.Depth).ThenBy(a => a.Id, StringComparer.Ordinal))
        {
            var seg = segments.FirstOrDefault(s =>
                anchor.Depth >= s.TopDepth - DepthEpsilon && anchor.Depth <= s.BottomDepth + DepthEpsilon);
            if (seg is null)
            {
                diagnostics.Add(new Diagnostic(
                    "anchor.outside-section",
                    $"Anchor {anchor.Id} at {anchor.Depth} {anchor.Datum} is outside every continuous segment; kept in place, not fitted.",
                    $"anchor:{anchor.Id}"));
                continue;
            }
            ((List<AgeAnchor>)seg.Anchors).Add(anchor);
            assigned.Add(anchor.Id);
        }

        foreach (var group in anchors.GroupBy(a => Math.Round(a.Depth, 6)).Where(g => g.Count() > 1))
        {
            var ids = string.Join(", ", group.Select(a => a.Id).OrderBy(x => x, StringComparer.Ordinal));
            diagnostics.Add(new Diagnostic(
                "anchor.depth-duplicate",
                $"Depth {group.Key} is shared by anchors {ids}; both retained, ordering is stable by id.",
                $"anchor:{group.Select(a => a.Id).OrderBy(x => x, StringComparer.Ordinal).First()}",
                "warning"));
        }
        return segments;
    }

    public static Result<ModelRunResult> Build(ModelInput input, ModelRunResult? previous = null)
    {
        var p = input.Parameters;
        if (p.Iterations > p.MaxIterations)
        {
            return Result.Failure<ModelRunResult>(ResultKind.BudgetExhausted,
                new Diagnostic("budget.exhausted",
                    $"Requested {p.Iterations} iterations exceeds budget {p.MaxIterations}.",
                    "parameters:iterations"));
        }

        var diagnostics = new List<Diagnostic>();
        var segments = BuildSegments(input.Sections, input.Boundaries, input.Anchors, diagnostics);
        var inputDigest = Canonical.InputDigest(input.Sections, input.Boundaries, input.Anchors, input.Events, p);
        var runId = $"run:{inputDigest[..12]}:{p.ParamVersion}";

        var previousSegments = previous?.Candidates
            .SelectMany(c => c.Segments.Select(s => (c.Kind, s)))
            .ToDictionary(x => (x.Kind, x.s.Digest), x => x.s)
            ?? new Dictionary<(string, string), SegmentResult>();

        var candidates = new List<CandidateResult>();
        var anySolvable = false;
        var anySegmentError = false;

        foreach (var kind in CandidateKinds.All)
        {
            var candidateDiagnostics = new List<Diagnostic>();
            var segmentResults = new List<SegmentResult>();
            foreach (var segment in segments)
            {
                var segDigest = SegmentDigest(kind, segment, p);
                if (previousSegments.TryGetValue((kind, segDigest), out var reused))
                {
                    segmentResults.Add(reused with { Reused = true });
                    anySolvable = true;
                    continue;
                }
                var built = BuildSegment(kind, segment, p, segDigest, candidateDiagnostics);
                if (built is null)
                {
                    anySegmentError = true;
                    continue;
                }
                anySolvable = true;
                segmentResults.Add(built);
            }
            var candDigest = Canonical.Sha256(kind + "|" + p.ParamVersion + "|" + inputDigest);
            candidates.Add(new CandidateResult(
                $"cand:{kind}:{candDigest[..12]}", kind, p.ParamVersion, segmentResults, candidateDiagnostics));
        }

        diagnostics.AddRange(candidates.SelectMany(c => c.Diagnostics));
        var run = new ModelRunResult(runId, inputDigest, p.ParamVersion, previous?.RunId, candidates, diagnostics);
        if (!anySolvable && anySegmentError)
            return Result.Failure<ModelRunResult>(ResultKind.SemanticallyUnsolvable, diagnostics.ToArray());
        return Result.Success(run, diagnostics);
    }

    public static string SegmentDigest(string kind, Segment segment, ModelParameters p)
    {
        var sb = new StringBuilder();
        sb.Append(kind).Append('|').Append(segment.Id).Append('|').Append(Canonical.Parameters(p)).Append('\n');
        foreach (var a in segment.Anchors.OrderBy(a => a.Id, StringComparer.Ordinal))
            sb.Append(Canonical.Anchor(a)).Append('\n');
        return Canonical.Sha256(sb.ToString());
    }

    private static SegmentResult? BuildSegment(
        string kind, Segment segment, ModelParameters p, string segDigest, List<Diagnostic> diagnostics)
    {
        var usable = new List<(AgeAnchor Anchor, double[] Draws)>();
        var rngSeed = SeedFrom(segDigest);

        foreach (var anchor in segment.Anchors.OrderBy(a => a.Depth).ThenBy(a => a.Id, StringComparer.Ordinal))
        {
            if (!AgeDist.IsConvertible(anchor.Distribution.Unit))
            {
                diagnostics.Add(new Diagnostic(
                    "anchor.unit-unconvertible",
                    $"Anchor {anchor.Id} uses unit '{anchor.Distribution.Unit}' which cannot be converted to yr BP; value left in place with diagnostic.",
                    $"anchor:{anchor.Id}"));
                continue;
            }
            var rng = new Random(rngSeed + StableIndex(anchor.Id));
            var draws = new double[p.Iterations];
            for (var i = 0; i < p.Iterations; i++)
            {
                var raw = AgeDist.Sample(anchor.Distribution, rng);
                draws[i] = AgeDist.ToYearsBp(raw, anchor.Distribution.Unit) - anchor.ReservoirOffsetYears;
            }
            usable.Add((anchor, draws));
        }

        if (usable.Count < 2)
        {
            diagnostics.Add(new Diagnostic(
                "segment.insufficient-anchors",
                $"Segment {segment.Id} has {usable.Count} usable anchor(s); at least 2 required. No curve fitted here.",
                $"segment:{segment.Id}", "warning"));
            return null;
        }

        // Inversion check: disjoint, ordered distributions in the wrong direction cannot be reconciled.
        for (var i = 0; i + 1 < usable.Count; i++)
        {
            var upper = usable[i].Draws.OrderBy(x => x).ToArray();
            var lower = usable[i + 1].Draws.OrderBy(x => x).ToArray();
            var upperMedian = upper[upper.Length / 2];
            var lowerMedian = lower[lower.Length / 2];
            var upperP25 = upper[(int)(0.025 * (upper.Length - 1))];
            var lowerP975 = lower[(int)(0.975 * (lower.Length - 1))];
            if (upperMedian > lowerMedian && upperP25 > lowerP975)
            {
                diagnostics.Add(new Diagnostic(
                    "segment.age-inversion",
                    $"Segment {segment.Id}: anchor {usable[i].Anchor.Id} (shallower) is disjointly younger than {usable[i + 1].Anchor.Id} (deeper); segment is semantically unsolvable, others unaffected.",
                    $"segment:{segment.Id}"));
                return null;
            }
        }

        var n = p.Iterations;
        var grid = BuildGrid(segment.TopDepth, segment.BottomDepth, p.GridStep);
        var depths = usable.Select(u => u.Anchor.Depth).ToArray();
        var samples = new double[n][];
        for (var i = 0; i < n; i++) samples[i] = new double[grid.Length];
        var ratePairs = new List<(double Top, double Bottom, double[] Rates)>();
        var rateDraws = usable.Select(_ => new double[n]).ToArray();

        for (var i = 0; i < n; i++)
        {
            // Isotonic (running-max) enforcement: age is monotonic non-decreasing with depth.
            var ages = new double[usable.Count];
            var running = double.NegativeInfinity;
            for (var k = 0; k < usable.Count; k++)
            {
                running = Math.Max(running, usable[k].Draws[i]);
                ages[k] = running;
            }
            for (var k = 0; k + 1 < usable.Count; k++)
            {
                var dAgeKa = (ages[k + 1] - ages[k]) / 1000.0;
                rateDraws[k][i] = dAgeKa <= 0 ? 0.0 : (depths[k + 1] - depths[k]) / dAgeKa;
            }
            (double Intercept, double Slope)? curve = kind == CandidateKinds.ConstantRate
                ? FitLine(depths, ages)
                : null;
            for (var g = 0; g < grid.Length; g++)
                samples[i][g] = curve is { } fit
                    ? Math.Max(0.0, fit.Intercept + fit.Slope * grid[g])
                    : Interp(depths, ages, grid[g]);
        }

        if (kind == CandidateKinds.Smoothed)
        {
            foreach (var row in samples)
            {
                var copy = (double[])row.Clone();
                for (var g = 0; g < row.Length; g++)
                {
                    var lo = Math.Max(0, g - 2);
                    var hi = Math.Min(row.Length - 1, g + 2);
                    var acc = 0.0;
                    for (var j = lo; j <= hi; j++) acc += copy[j];
                    row[g] = acc / (hi - lo + 1);
                }
            }
        }

        var median = new double[grid.Length];
        var p25 = new double[grid.Length];
        var p975 = new double[grid.Length];
        var col = new double[n];
        for (var g = 0; g < grid.Length; g++)
        {
            for (var i = 0; i < n; i++) col[i] = samples[i][g];
            Array.Sort(col);
            median[g] = col[n / 2];
            p25[g] = col[(int)(0.025 * (n - 1))];
            p975[g] = col[(int)(0.975 * (n - 1))];
        }

        var rates = new List<RateInterval>();
        for (var k = 0; k + 1 < usable.Count; k++)
        {
            var draws = rateDraws[k].OrderBy(x => x).ToArray();
            rates.Add(new RateInterval(
                $"rate:{segDigest[..12]}:{k}", segment.Id, depths[k], depths[k + 1],
                draws[draws.Length / 2], draws[(int)(0.025 * (draws.Length - 1))],
                draws[(int)(0.975 * (draws.Length - 1))], "m/ka"));
        }

        return new SegmentResult(
            $"sr:{segDigest[..16]}", segment, segDigest, false,
            grid, median, p25, p975, rates, Array.Empty<Diagnostic>());
    }

    private static double[] BuildGrid(double top, double bottom, double step)
    {
        var count = (int)Math.Ceiling((bottom - top) / step) + 1;
        count = Math.Clamp(count, 2, 2000);
        var grid = new double[count];
        for (var i = 0; i < count; i++)
            grid[i] = Math.Min(bottom, top + i * step);
        grid[^1] = bottom;
        return grid;
    }

    private static (double Intercept, double Slope) FitLine(double[] x, double[] y)
    {
        var xm = x.Average();
        var ym = y.Average();
        var sxy = 0.0;
        var sxx = 0.0;
        for (var i = 0; i < x.Length; i++)
        {
            sxy += (x[i] - xm) * (y[i] - ym);
            sxx += (x[i] - xm) * (x[i] - xm);
        }
        var slope = sxx == 0 ? 0 : sxy / sxx;
        return (ym - slope * xm, slope);
    }

    private static double Interp(double[] depths, double[] ages, double depth)
    {
        if (depth <= depths[0])
            return Extrapolate(depths[0], ages[0], depths[1], ages[1], depth);
        if (depth >= depths[^1])
            return Extrapolate(depths[^2], ages[^2], depths[^1], ages[^1], depth);
        for (var i = 0; i + 1 < depths.Length; i++)
        {
            if (depth >= depths[i] && depth <= depths[i + 1])
            {
                var span = depths[i + 1] - depths[i];
                var t = span <= 0 ? 0 : (depth - depths[i]) / span;
                return ages[i] + t * (ages[i + 1] - ages[i]);
            }
        }
        return ages[^1];
    }

    private static double Extrapolate(double d0, double a0, double d1, double a1, double depth)
    {
        var span = d1 - d0;
        if (span <= 0) return a0;
        return Math.Max(0.0, a0 + (a1 - a0) / span * (depth - d0));
    }

    private static int SeedFrom(string digest)
        => BitConverter.ToInt32(Convert.FromHexString(digest[..8]));

    private static int StableIndex(string id)
    {
        unchecked
        {
            var h = 17;
            foreach (var c in id) h = h * 31 + c;
            return h & 0x7FFF;
        }
    }
}
