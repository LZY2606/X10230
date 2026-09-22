using System.Text;
using System.Text.Json;

namespace ChronoAge.Core;

public sealed record SegmentCurve(
    string SegmentId,
    IReadOnlyList<AnchorCurvePoint> Points,
    IReadOnlyList<RateInterval> Rates,
    double MonotonicityProbability,
    IReadOnlyList<MonotonicityBreach> Breaches);

public sealed record AnchorCurvePoint(
    string ValueId,
    string AnchorId,
    double DepthM,
    double Q025,
    double Q500,
    double Q975,
    double Mean,
    bool InGap);

public sealed record RateInterval(
    string ValueId,
    string SegmentId,
    double FromDepthM,
    double ToDepthM,
    double MedianCmYr,
    double Q025,
    double Q975,
    double PlausibilityPenalty,
    bool Plausible);

public sealed record MonotonicityBreach(string SegmentId, string UpperAnchorId, string LowerAnchorId, double Frequency, string Reason);

public sealed record CandidateResult(
    string CandidateId,
    string Genealogy,
    bool Feasible,
    double Score,
    double MonotonicityProbability,
    IReadOnlyList<SegmentCurve> Segments,
    IReadOnlyList<Diagnostic> Notices,
    bool EquiOptimal,
    int Rank);

public sealed record DerivationArtifact(
    string ArtifactId,
    string RuleVersion,
    string InputHash,
    IReadOnlyList<string> DependsOn,
    IReadOnlyList<CandidateResult> Results,
    IReadOnlyList<DisplayValue> DisplayValues,
    IReadOnlyList<Diagnostic> Diagnostics,
    long SamplesUsed,
    long SampleBudget,
    string AtUtc);

/// <summary>Every number shown in the review UI is emitted as a stable, labelled display value.</summary>
public sealed record DisplayValue(
    string ValueId,
    string Kind,
    string Label,
    double? Numeric,
    string Unit,
    string? Datum,
    double? ValidFromDepthM,
    double? ValidToDepthM,
    string? FailureReason,
    string SourceRef);

/// <summary>
/// Monte-Carlo depth-age solver. Monotonicity (older downward) is enforced/checked ONLY inside
/// continuously deposited segments; gaps and unconformities terminate curves. The long tail of
/// distributions survives in quantile bands — it is never replaced by a mean.
/// </summary>
public sealed class Solver
{
    private static readonly double[] BandQs = { 0.025, 0.5, 0.975 };

    public DerivationArtifact Solve(
        ProjectState st,
        IReadOnlyList<DepoSegment> segments,
        IReadOnlyList<ModelCandidate> candidates,
        IReadOnlyList<DerivedAnchor> anchors,
        string inputHash,
        IReadOnlyList<string> dependsOn,
        DateTimeOffset atUtc)
    {
        var p = st.Parameters;
        long samplesUsed = 0;
        var globalDiags = new List<Diagnostic>();
        var n = Math.Max(8, p.SamplesPerAnchor);
        var results = new List<CandidateResult>();
        var display = new List<DisplayValue>();

        var needed = (long)candidates.Count * anchors.Count * n;
        if (needed > p.SampleBudget)
        {
            globalDiags.Add(Diag.Error("SOLVE_BUDGET",
                $"Derivation needs ~{needed} samples but the budget is {p.SampleBudget} (rule {p.Version}). Tighten the candidate set or raise the budget via a parameter revision.",
                null, "sample_budget"));
            var empty = new DerivationArtifact(
                StableIds.Short("ART", $"budget|{inputHash}|{p.Version}"), p.Version, inputHash,
                dependsOn, Array.Empty<CandidateResult>(), Array.Empty<DisplayValue>(),
                globalDiags, samplesUsed, p.SampleBudget, atUtc.ToString("O"));
            return empty;
        }

        foreach (var cand in candidates)
        {
            var curves = new List<SegmentCurve>();
            var notices = new List<Diagnostic>();
            double segMonoMin = 1.0;
            double totalPenalty = 0;

            foreach (var seg in segments)
            {
                var segAnchors = cand.AnchorsInOrder
                    .Where(a => a.DepthM >= seg.TopDepthM - p.DepthEpsilonM
                             && a.DepthM <= seg.BottomDepthM + p.DepthEpsilonM)
                    .OrderBy(a => a.DepthM).ThenBy(a => a.AnchorId, StringComparer.Ordinal)
                    .ToList();

                if (segAnchors.Count < 2)
                {
                    curves.Add(new SegmentCurve(seg.SegmentId, Array.Empty<AnchorCurvePoint>(),
                        Array.Empty<RateInterval>(), 1.0, Array.Empty<MonotonicityBreach>()));
                    continue;
                }

                var sampleCols = new double[segAnchors.Count][];
                for (var ai = 0; ai < segAnchors.Count; ai++)
                {
                    var seed = unchecked(p.RngSeed
                        + StableIds.ContentHash(cand.CandidateId).Take(8).Aggregate(0L, (acc, c) => acc * 31 + c)
                        + StableIds.ContentHash(segAnchors[ai].AnchorId).Take(8).Aggregate(0L, (acc, c) => acc * 17 + c));
                    sampleCols[ai] = DistributionSampler.Sample(segAnchors[ai].Distribution, n, seed);
                    samplesUsed += n;
                }

                var points = new List<AnchorCurvePoint>();
                var breaches = new List<MonotonicityBreach>();
                for (var ai = 0; ai < segAnchors.Count; ai++)
                {
                    DistributionSampler.TryQuantiles(sampleCols[ai], BandQs, out var q);
                    var mean = sampleCols[ai].Average();
                    var pvId = $"DV-{cand.CandidateId[..11]}-{seg.SegmentId}-A{ai + 1:00}";
                    points.Add(new AnchorCurvePoint(pvId, segAnchors[ai].AnchorId,
                        segAnchors[ai].DepthM, q[0], q[1], q[2], mean, false));
                    display.Add(new DisplayValue(pvId, "anchor_age",
                        $"{segAnchors[ai].AnchorId} age q50", q[1], "yr_BP", null,
                        segAnchors[ai].DepthM, segAnchors[ai].DepthM, null,
                        segAnchors[ai].SourceLogId));
                    DetectLongTail(pvId, sampleCols[ai], q, segAnchors[ai], notices, display);

                    if (ai > 0)
                    {
                        var bad = 0L;
                        for (var s = 0; s < n; s++)
                            // ages must be non-decreasing downward within one continuous segment
                            if (sampleCols[ai][s] < sampleCols[ai - 1][s] - 1e-9) bad++;
                        var freq = (double)bad / n;
                        if (freq > 0.001)
                            breaches.Add(new MonotonicityBreach(seg.SegmentId,
                                segAnchors[ai - 1].AnchorId, segAnchors[ai].AnchorId, freq,
                                "younger_age_below_older_within_continuous_segment"));
                    }
                }

                var rates = new List<RateInterval>();
                for (var ai = 1; ai < segAnchors.Count; ai++)
                {
                    var depthSpanM = segAnchors[ai].DepthM - segAnchors[ai - 1].DepthM;
                    var rateSamples = new double[n];
                    for (var s = 0; s < n; s++)
                    {
                        var ageSpanYr = sampleCols[ai][s] - sampleCols[ai - 1][s];
                        // rate cm/yr = (cm)/(yr). Negative samples are kept (they drive breach stats).
                        rateSamples[s] = depthSpanM <= 1e-9 ? double.NaN : (depthSpanM * 100.0) / ageSpanYr;
                    }
                    var valid = rateSamples.Where(r => !double.IsNaN(r) && double.IsFinite(r)).ToArray();
                    if (valid.Length == 0)
                    {
                        notices.Add(Diag.Warn("SOLVE_TIE_SPAN",
                            $"Zero depth span between {segAnchors[ai - 1].AnchorId} and {segAnchors[ai].AnchorId}; rate undefined (tie).",
                            null, "rate"));
                        continue;
                    }
                    DistributionSampler.TryQuantiles(valid, BandQs, out var rq);
                    var penalty = RatePenalty(rq[1], p);
                    var rvId = $"DV-{cand.CandidateId[..11]}-{seg.SegmentId}-R{ai:00}";
                    var interval = new RateInterval(rvId, seg.SegmentId,
                        segAnchors[ai - 1].DepthM, segAnchors[ai].DepthM,
                        rq[1], rq[0], rq[2], penalty, penalty == 0);
                    rates.Add(interval);
                    totalPenalty += penalty;
                    display.Add(new DisplayValue(rvId, "sed_rate",
                        $"rate {seg.SegmentId} {segAnchors[ai - 1].AnchorId}->{segAnchors[ai].AnchorId}",
                        rq[1], "cm/yr", null, interval.FromDepthM, interval.ToDepthM,
                        penalty > 0 ? "outside_plausible_rate_range" : null,
                        $"{segAnchors[ai - 1].SourceLogId},{segAnchors[ai].SourceLogId}"));
                }

                var monoProb = breaches.Count == 0 ? 1.0
                    : 1.0 - breaches.Max(b => b.Frequency);
                segMonoMin = Math.Min(segMonoMin, monoProb);
                curves.Add(new SegmentCurve(seg.SegmentId, points, rates, monoProb, breaches));
            }

            var feasible = segMonoMin >= 0.95;
            var score = Math.Round(segMonoMin * 1000.0 - totalPenalty, 6);
            results.Add(new CandidateResult(cand.CandidateId, cand.Genealogy, feasible, score,
                segMonoMin, curves, notices, false, 0));
        }

        // Deterministic ranking; equi-optimal candidates are flagged and all retained.
        var ordered = results.OrderByDescending(r => r.Score).ThenBy(r => r.CandidateId, StringComparer.Ordinal).ToList();
        var bestScore = ordered.Count > 0 ? ordered[0].Score : 0;
        for (var i = 0; i < ordered.Count; i++)
        {
            var eq = ordered[i].Feasible && Math.Abs(ordered[i].Score - bestScore) < 1e-9;
            ordered[i] = ordered[i] with { Rank = i + 1, EquiOptimal = eq };
        }

        if (ordered.Count > 0 && ordered[0].MonotonicityProbability < 0.95)
            globalDiags.Add(Diag.Warn("SOLVE_NO_FEASIBLE",
                "No candidate reaches 95% within-segment monotonicity; all candidates retained with breach evidence.",
                null, "monotonicity"));

        var artifactId = StableIds.Short("ART",
            $"{inputHash}|{p.Version}|{string.Join(",", dependsOn.OrderBy(x => x, StringComparer.Ordinal))}");
        return new DerivationArtifact(artifactId, p.Version, inputHash, dependsOn,
            ordered, display, globalDiags, samplesUsed, p.SampleBudget, atUtc.ToString("O"));
    }

    private static double RatePenalty(double medianRate, ModelParameters p)
    {
        if (medianRate < 0) return 1000 + Math.Abs(medianRate);
        if (medianRate < p.PlausibleRateMin) return (p.PlausibleRateMin - medianRate) / p.PlausibleRateMin * 10;
        if (medianRate > p.PlausibleRateMax) return (medianRate - p.PlausibleRateMax) / p.PlausibleRateMax * 10;
        return 0;
    }

    private static void DetectLongTail(string pvId, double[] samples, double[] q, DerivedAnchor anchor,
        List<Diagnostic> notices, List<DisplayValue> display)
    {
        var iqr = q[2] - q[0];
        // Right tail reaching far beyond the median while the lower band stays close is evidence
        // a mean would hide; we keep the quantile and emit an explicit diagnostic.
        if (iqr > 0 && (q[2] - q[1]) > 3.0 * (q[1] - q[0]))
        {
            notices.Add(Diag.Warn("SOLVE_LONG_TAIL",
                $"Anchor {anchor.AnchorId} has a pronounced long right tail (q975-q50 >> q50-q025); band retained, no mean substitution.",
                anchor.SourceLogId, "distribution"));
            display.Add(new DisplayValue(pvId + "-tail", "tail_asymmetry",
                $"{anchor.AnchorId} right-tail asymmetry", (q[2] - q[1]) / Math.Max(iqr, 1e-9),
                "ratio", null, anchor.DepthM, anchor.DepthM, null, anchor.SourceLogId));
        }
    }
}
