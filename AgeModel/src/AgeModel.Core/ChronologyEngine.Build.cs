using System.Globalization;

namespace AgeModel.Core;

public sealed partial class ChronologyEngine
{
    private CandidateModel BuildCandidate(ProjectState state, IReadOnlyList<AnchorView> ordered, string hash, int zeroBasedRank, List<Diagnostic> diagnostics)
    {
        var rates = new List<RateIntervalView>();
        var band = ordered.Select(a => new BandPoint(
            $"band-{hash[..8]}-{a.Anchor.Id}",
            $"candidate-{hash[..12]}",
            a.Anchor.Id,
            a.DepthM,
            a.Q01,
            a.Q05,
            a.MedianAge,
            a.Q95,
            a.Q99)).ToList();

        foreach (var pair in ordered.Zip(ordered.Skip(1), (a, b) => (a, b)))
        {
            var boundary = CrossingBoundary(a: pair.a.DepthM, b: pair.b.DepthM, state);
            if (boundary is not null) continue;
            var segment = state.Package.Segments.FirstOrDefault(s => InClosedSegment(pair.a.DepthM, s) && InClosedSegment(pair.b.DepthM, s));
            if (segment is null)
            {
                diagnostics.Add(Diagnostic("RATE_ACROSS_SEGMENT", "warning",
                    $"anchors[{pair.a.Anchor.Id}]", pair.a.Anchor.Id,
                    "Rate interval skipped because anchors do not share one uninterrupted segment."));
                continue;
            }

            var thicknessCm = Math.Abs(pair.b.DepthM - pair.a.DepthM) * 100.0;
            var id = $"rate-{hash[..8]}-{pair.a.Anchor.Id}-{pair.b.Anchor.Id}";
            rates.Add(new RateIntervalView(
                id,
                $"candidate-{hash[..12]}",
                segment.Id,
                pair.a.Anchor.Id,
                pair.b.Anchor.Id,
                pair.a.DepthM,
                pair.b.DepthM,
                RateCmPerKyr(thicknessCm, pair.b.MedianAge - pair.a.MedianAge),
                RateCmPerKyr(thicknessCm, pair.b.Q95 - pair.a.Q05),
                RateCmPerKyr(thicknessCm, pair.b.Q05 - pair.a.Q95),
                RateCmPerKyr(thicknessCm, pair.b.Q99 - pair.a.Q01),
                RateCmPerKyr(thicknessCm, pair.b.Q01 - pair.a.Q99),
                $"{pair.a.DepthM.ToString("0.###", CultureInfo.InvariantCulture)}-{pair.b.DepthM.ToString("0.###", CultureInfo.InvariantCulture)} m {state.Package.DepthDatum.CoordinateDatum}",
                new[] { pair.a.Anchor.Id, pair.b.Anchor.Id }));
        }

        var exclusions = state.Exclusions.Values
            .Where(e => e.Status == "excluded" && e.SubjectType == "anchor")
            .Select(e => e.Id)
            .Order(StringComparer.Ordinal)
            .ToArray();
        return new CandidateModel(
            $"candidate-{hash[..12]}",
            zeroBasedRank + 1,
            false,
            false,
            false,
            0,
            hash,
            state.Parameters.Version,
            ordered.Select(a => a.Anchor.Id).ToArray(),
            exclusions,
            ordered.Select(a => a.SegmentId).Distinct().Order(StringComparer.Ordinal).ToArray(),
            rates,
            band,
            Array.Empty<Diagnostic>());
    }

    private static double RateCmPerKyr(double thicknessCm, double ageSpanYears)
    {
        if (Math.Abs(ageSpanYears) < 1e-12) return 0;
        if (ageSpanYears < 0) return double.NaN;
        return thicknessCm / (ageSpanYears / 1000.0);
    }

    private static IReadOnlyList<CandidateModel> RankAndAdopt(IReadOnlyList<CandidateModel> candidates, ProjectState state)
    {
        var scored = candidates
            .Select(c =>
            {
                var distributionSpread = c.Band.Sum(b => b.Q95 - b.Q05);
                var rateSpread = c.Rates.Sum(r => Math.Max(0, r.HighRateCmPerKyr - r.LowRateCmPerKyr));
                var fewerExclusions = c.ProposedExclusionIds.Count;
                var score = distributionSpread + 0.01 * rateSpread + 1000.0 * fewerExclusions;
                return c with { Score = score };
            })
            .OrderBy(c => c.Score)
            .ThenBy(c => c.MaterialHash, StringComparer.Ordinal)
            .ThenBy(c => string.Join(",", c.IncludedAnchorIds), StringComparer.Ordinal)
            .Select((c, index) => c with { Rank = index + 1 })
            .ToList();

        var bestScore = scored[0].Score;
        for (var i = 0; i < scored.Count; i++)
        {
            var tieWithPrevious = i > 0 && Math.Abs(scored[i - 1].Score - scored[i].Score) < 1e-9;
            var tieWithNext = i + 1 < scored.Count && Math.Abs(scored[i + 1].Score - scored[i].Score) < 1e-9;
            scored[i] = scored[i] with
            {
                TieWithNext = tieWithNext,
                Adopted = i == 0 || (tieWithPrevious && Math.Abs(scored[i].Score - bestScore) < 1e-9)
            };
        }
        return scored;
    }

    private static List<Diagnostic> ValidateInclusions(ProjectState state, IReadOnlyList<AnchorView> anchors)
    {
        var diagnostics = new List<Diagnostic>();
        if (anchors.Count == 0)
        {
            diagnostics.Add(Diagnostic("NO_ANCHORS", "error", "anchors", null, "At least one anchor distribution is required."));
            return diagnostics;
        }

        foreach (var anchor in anchors.Where(a => !a.Used && a.ExclusionReason is not null))
        {
            diagnostics.Add(Diagnostic("ANCHOR_EXCLUDED", "info", $"anchors[{anchor.Anchor.Id}]", anchor.Anchor.Id,
                $"Excluded decision retained: {anchor.ExclusionReason}"));
        }

        foreach (var segment in state.Package.Segments)
        {
            var used = anchors.Where(a => a.Used && a.SegmentId == segment.Id).ToList();
            if (used.Count < state.Parameters.MinimumAnchorsPerInterval)
            {
                diagnostics.Add(Diagnostic("UNDERCONSTRAINED_SEGMENT", "error", $"segments[{segment.Id}]", segment.Id,
                    $"Segment has {used.Count} usable anchors; {state.Parameters.MinimumAnchorsPerInterval} required."));
            }
        }

        if (anchors.Any(a => a.AgeDistribution.Samples.Count > state.Parameters.MaxDistributionSamples))
        {
            diagnostics.Add(Diagnostic("DISTRIBUTION_BUDGET", "error", "parameters.maxDistributionSamples", null,
                "An anchor distribution exceeds the retained-sample resource budget."));
        }
        return diagnostics;
    }
}
