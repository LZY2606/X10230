using System.Diagnostics;
using Chrono.Core.Materials;

namespace Chrono.Core.Engine;

public sealed record FitOutcome(
    List<ModelCandidate> Candidates,
    List<BuildDiagnostic> Notices,
    int SamplesUsed,
    bool BudgetExceeded);

public sealed class CandidateFitter
{
    private const int BandPoints = 32;

    public FitOutcome Fit(IReadOnlyList<ChronologyChain> chains,
        ChronoParameters parameters, BuildBudget budget, Stopwatch clock)
    {
        int samples = Math.Min(parameters.MonteCarloSamples, budget.MaxSamples);
        var probs = StratifiedProbabilities(samples);
        var candidates = new List<ModelCandidate>();
        var notices = new List<BuildDiagnostic>();
        int usedSamples = 0;
        bool budgetExceeded = parameters.MonteCarloSamples > budget.MaxSamples;

        foreach (var chain in chains)
        {
            var variants = EnumerateVariants(chain);
            var families = new[] { "linear", "monotone-spline" };

            foreach (var variant in variants)
            {
                foreach (var family in families)
                {
                    if (candidates.Count >= budget.MaxCandidates)
                    {
                        budgetExceeded = true;
                        break;
                    }

                    var fit = FitOne(chain, variant, family, parameters, probs,
                        (s) =>
                        {
                            usedSamples += s;
                            if (clock.Elapsed > budget.TimeLimit) budgetExceeded = true;
                        });
                    if (fit.LongTail)
                    {
                        notices.Add(new BuildDiagnostic("LONGTail-AGE-DISTRIBUTION",
                            DiagnosticSeverity.Warning, chain.ChainId, "anchor", null,
                            "one or more anchors carry a long-tailed age distribution; quantiles propagated, no mean substitution",
                            null));
                    }
                    candidates.Add(fit.Candidate);
                }
                if (budgetExceeded) break;
            }
            if (budgetExceeded) break;
        }

        if (budgetExceeded)
            return new FitOutcome(candidates, notices, usedSamples, true);

        double best = candidates.Min(c => c.Score);
        bool tied = candidates.Count(c => Math.Abs(c.Score - best) < 1e-9) > 1;

        var ranked = candidates
            .OrderBy(c => c.Score)
            .ThenBy(c => c.Family, StringComparer.Ordinal)
            .ThenBy(c => c.ChainId, StringComparer.Ordinal)
            .ThenBy(c => c.AnchorIds.Count == 0 ? "" : string.Join(",", c.AnchorIds), StringComparer.Ordinal)
            .Select((c, i) =>
            {
                var stableId = StableId(c);
                return c with { Rank = i + 1, Tied = Math.Abs(c.Score - best) < 1e-9, CandidateId = stableId };
            })
            .ToList();

        if (tied)
            notices.Add(new BuildDiagnostic("MULTIPLE-EQUALLY-GOOD-MODELS", DiagnosticSeverity.Info,
                "engine", "ranking", null,
                "multiple equally defensible models share the best score; all retained", null));

        return new FitOutcome(ranked, notices, usedSamples, false);
    }

    private static string StableId(ModelCandidate c)
    {
        var material = $"{c.ChainId}|{c.Family}|{string.Join(",", c.AnchorIds.OrderBy(x => x))}";
        var hash = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(
            System.Text.Encoding.UTF8.GetBytes(material)))[..12].ToLowerInvariant();
        return $"model-{hash}";
    }

    private static double[] StratifiedProbabilities(int samples)
    {
        var p = new double[samples];
        for (int i = 0; i < samples; i++)
            p[i] = (i + 0.5) / samples;
        return p;
    }

    private static List<List<Anchor>> EnumerateVariants(ChronologyChain chain)
    {
        // Within one chain, every active anchor participates. Variant combinatorics are
        // reserved for explicit duplicate-depth groups; ties stay jointly present and are
        // reported, never silently averaged.
        var variant = new List<List<Anchor>> { chain.Anchors.ToList() };
        return variant;
    }

    private static (ModelCandidate Candidate, bool LongTail) FitOne(
        ChronologyChain chain, List<Anchor> anchors, string family,
        ChronoParameters parameters, double[] probs, Action<int> chargeSamples)
    {
        var ordered = anchors.OrderBy(a => a.Depth.Meters)
            .ThenBy(a => a.Id, StringComparer.Ordinal).ToList();

        var draws = new double[ordered.Count][];
        bool longTail = false;
        for (int i = 0; i < ordered.Count; i++)
        {
            var dist = ordered[i].CorrectedAge ?? ordered[i].MeasuredAge;
            longTail |= dist.IsLongTail();
            draws[i] = probs.Select(p => dist.Quantile(p)).ToArray();
            chargeSamples(probs.Length);
        }

        // Per-draw monotone projection (isotonic regression) within this unbroken chain.
        int feasible = 0;
        double totalMismatch = 0;
        var sampleBands = new List<double[]>();
        var depthTop = ordered.First().Depth.Meters;
        var depthBottom = ordered.Last().Depth.Meters;
        var bandDepths = Enumerable.Range(0, BandPoints)
            .Select(k => depthTop + (depthBottom - depthTop) * k / (BandPoints - 1.0))
            .ToArray();

        for (int s = 0; s < probs.Length; s++)
        {
            var ages = new double[ordered.Count];
            for (int i = 0; i < ordered.Count; i++) ages[i] = draws[i][s];
            var projected = IsotonicDescending(ages);

            bool feasibleDraw = true;
            double mismatch = 0;
            for (int i = 0; i < ordered.Count; i++)
            {
                if (Math.Abs(projected[i] - ages[i]) > 1e-6) feasibleDraw = false;
                mismatch += Math.Abs(projected[i] - ages[i]);
            }
            if (feasibleDraw) feasible++;
            totalMismatch += mismatch;

            sampleBands.Add(bandDepths.Select(d => Interpolate(family, ordered, projected, d)).ToArray());
        }

        var band = new List<UncertaintyBandPoint>();
        for (int k = 0; k < BandPoints; k++)
        {
            var column = sampleBands.Select(row => row[k]).OrderBy(v => v).ToArray();
            band.Add(new UncertaintyBandPoint(bandDepths[k],
                QuantileSorted(column, 0.025), QuantileSorted(column, 0.5),
                QuantileSorted(column, 0.975), longTail));
        }

        var rates = new List<RateInterval>();
        for (int i = 1; i < ordered.Count; i++)
        {
            double depthGap = ordered[i].Depth.Meters - ordered[i - 1].Depth.Meters;
            var lower = probs
                .Select((p, s) => RateCmPerKa(depthGap, draws[i - 1][s], draws[i][s], lower: true))
                .OrderBy(v => v).ToArray();
            var upper = probs
                .Select((p, s) => RateCmPerKa(depthGap, draws[i - 1][s], draws[i][s], lower: false))
                .OrderBy(v => v).ToArray();
            rates.Add(new RateInterval(
                QuantileSorted(lower, 0.05), QuantileSorted(upper, 0.95)));
        }

        double fit = totalMismatch / Math.Max(1, probs.Length);
        double coverage = 100.0 * feasible / probs.Length;
        double score = fit + parameters.GapPenaltyWeight * (100 - coverage) * 0.01;

        var candidate = new ModelCandidate(
            "", chain.ChainId, family, 0, false, score, fit, coverage,
            rates, band, ordered.Select(a => a.Id).ToList());
        return (candidate, longTail);
    }

    private static double RateCmPerKa(double depthMeters, double ageShallowCalBp,
        double ageDeepCalBp, bool lower)
    {
        double ageGap = ageShallowCalBp - ageDeepCalBp;
        // Depth increases downward, so age gap is shallow minus deep (older down-section).
        if (ageGap <= 0) return lower ? double.PositiveInfinity : 0;
        double cm = depthMeters * 100.0;
        return cm / (ageGap / 1000.0);
    }

    private static double[] IsotonicDescending(double[] values)
    {
        // Pool-Adjacent-Violators enforcing non-increasing values (older down-core).
        var result = values.ToArray();
        var weights = new int[values.Length];
        for (int i = 0; i < weights.Length; i++) weights[i] = 1;
        bool changed = true;
        while (changed)
        {
            changed = false;
            for (int i = 0; i < result.Length - 1; i++)
            {
                if (result[i] < result[i + 1])
                {
                    double pooled = (result[i] * weights[i] + result[i + 1] * weights[i + 1])
                        / (weights[i] + weights[i + 1]);
                    result[i] = pooled;
                    result[i + 1] = pooled;
                    weights[i] += weights[i + 1];
                    weights[i + 1] = weights[i];
                    changed = true;
                }
            }
        }
        return result;
    }

    private static double Interpolate(string family, List<Anchor> anchors,
        double[] ages, double depthMeters)
    {
        if (family == "linear") return LinearInterp(anchors, ages, depthMeters);
        return PchipInterp(anchors, ages, depthMeters);
    }

    private static double LinearInterp(List<Anchor> anchors, double[] ages, double d)
    {
        var xs = anchors.Select(a => a.Depth.Meters).ToArray();
        if (d <= xs[0]) return ages[0];
        if (d >= xs[^1]) return ages[^1];
        for (int i = 0; i < xs.Length - 1; i++)
        {
            if (d >= xs[i] && d <= xs[i + 1])
 {
                    double t = (d - xs[i]) / (xs[i + 1] - xs[i]);
                    return ages[i] + (ages[i + 1] - ages[i]) * t;
                }
        }
        return ages[^1];
    }

    private static double PchipInterp(List<Anchor> anchors, double[] ages, double d)
    {
        var xs = anchors.Select(a => a.Depth.Meters).ToArray();
        if (d <= xs[0]) return ages[0];
        if (d >= xs[^1]) return ages[^1];
        var slopes = new double[xs.Length - 1];
        for (int i = 0; i < slopes.Length; i++)
            slopes[i] = (ages[i + 1] - ages[i]) / (xs[i + 1] - xs[i]);
        var deriv = new double[xs.Length];
        deriv[0] = slopes[0];
        deriv[^1] = slopes[^1];
        for (int i = 1; i < xs.Length - 1; i++)
        {
            if (slopes[i - 1] * slopes[i] <= 0) deriv[i] = 0;
            else
            {
                double h1 = xs[i] - xs[i - 1];
                double h2 = xs[i + 1] - xs[i];
                deriv[i] = 3 / ((h1 + h2) / (h1 * slopes[i - 1]) + (h1 + h2) / (h2 * slopes[i]));
            }
        }
        for (int i = 0; i < xs.Length - 1; i++)
        {
            if (d >= xs[i] && d <= xs[i + 1])
            {
                double h = xs[i + 1] - xs[i];
                double t = (d - xs[i]) / h;
                double t2 = t * t, t3 = t2 * t;
                double h00 = 2 * t3 - 3 * t2 + 1;
                double h10 = t3 - 2 * t2 + t;
                double h01 = -2 * t3 + 3 * t2;
                double h11 = t3 - t2;
                return h00 * ages[i] + h10 * h * deriv[i]
                    + h01 * ages[i + 1] + h11 * h * deriv[i + 1];
            }
        }
        return ages[^1];
    }

    private static double QuantileSorted(double[] sorted, double p)
    {
        if (sorted.Length == 0) return double.NaN;
        int idx = (int)Math.Clamp(Math.Floor(p * sorted.Length), 0, sorted.Length - 1);
        return sorted[idx];
    }
}
