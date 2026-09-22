using ChronoCore.Domain;

namespace ChronoCore.Core;

public sealed record AgeCurvePoint(string Id, double DepthM, double? MedianYrBp, double? P05YrBp, double? P95YrBp, int RunIndex);

public sealed record RateInterval(
    string Id, int RunIndex, double DepthTopM, double DepthBottomM,
    double MedianCmPerKyr, double LowCmPerKyr, double HighCmPerKyr, string Unit);

public sealed record CandidateModel(
    string ModelId,
    string CoreId,
    string Kernel,
    string RuleVersion,
    string Variant,                  // "full" | "tie:<key>:<anchorId>"
    IReadOnlyList<string> AnchorIds,
    IReadOnlyList<AgeCurvePoint> Curve,
    IReadOnlyList<RateInterval> Rates,
    double Score,
    string Status,                   // "ok" | "rejected:semantic-unsolvable"
    string? RejectReason);

public sealed record SamplingOutcome(string Status, string? Reason, IReadOnlyList<double>? Quantiles);

/// <summary>
/// Builds every defensible depth-age candidate. Age is monotonic only within uninterrupted runs;
/// explicit boundaries break interpolation. Distributions are never collapsed to their mean.
/// </summary>
public static class CandidateEngine
{
    public const string Ok = "ok";
    public const string SemanticUnsolvable = "rejected:semantic-unsolvable";

    public static IReadOnlyList<CandidateModel> Build(NormalizedProfile profile, RuleSet rules)
    {
        var models = new List<CandidateModel>();
        var tieVariants = EnumerateTieVariants(profile);

        foreach (var kernel in rules.Kernels)
        {
            foreach (var variant in tieVariants)
            {
                models.Add(BuildOne(profile, rules, kernel, variant));
            }
        }

        return models;
    }

    private sealed record TieVariant(string Key, IReadOnlyList<AnchorPick> Picks);
    private sealed record AnchorPick(string TieKey, string? ChosenAnchor); // null => intersect all

    private static IEnumerable<TieVariant> EnumerateTieVariants(NormalizedProfile profile)
    {
        var resolvable = profile.TieGroups.Where(g => g.Resolution == "intersect").ToList();
        if (resolvable.Count == 0)
        {
            yield return new TieVariant("full", Array.Empty<AnchorPick>());
            yield break;
        }
        // full = every tie group intersected
        yield return new TieVariant("full", resolvable.Select(g => new AnchorPick(g.Key, null)).ToList());
        // one variant per single-anchor choice inside each tie group; the other groups stay intersected
        foreach (var g in resolvable)
        {
            foreach (var anchorId in g.AnchorIds)
            {
                var picks = resolvable.Select(x =>
                    x.Key == g.Key ? new AnchorPick(g.Key, anchorId) : new AnchorPick(x.Key, null)).ToList();
                yield return new TieVariant($"tie:{g.Key}:{anchorId}", picks);
            }
        }
    }

    private static CandidateModel BuildOne(NormalizedProfile profile, RuleSet rules, string kernel, TieVariant variant)
    {
        var modelId = "model:" + StableId.Of(profile.CoreId, rules.Version, kernel, variant.Key,
            string.Join(",", variant.Picks.Select(p => $"{p.TieKey}={p.ChosenAnchor ?? "intersect"}")));

        var curve = new List<AgeCurvePoint>();
        var rates = new List<RateInterval>();
        var widthSum = 0.0;
        var widthN = 0;
        var blockReasons = new List<string>();

        foreach (var run in profile.Runs)
        {
            var anchors = AnchorsForRun(profile, run, variant);
            var blockers = BlockingDiagnostics(profile, run);
            if (blockers.Count > 0)
            {
                blockReasons.AddRange(blockers);
                continue;
            }
            if (anchors.Count == 0) continue;
            if (anchors.Count == 1)
            {
                // under-constrained run: no curve invented, no silent constant fill
                continue;
            }
            if (!MediansOrdered(anchors))
            {
                blockReasons.Add($"run {run.RunIndex}: anchor medians are not ordered with depth");
                continue;
            }

            var bottom = Math.Min(run.BottomM, AnchorsExtent(profile, run).bottom);
            var top = Math.Max(run.TopM, AnchorsExtent(profile, run).top);
            var steps = Math.Max(2, (int)Math.Round(rules.GridCount * (bottom - top) / Math.Max(1e-9, profile.Runs.Max(r => r.BottomM))));
            AgeCurvePoint? prev = null;
            for (var i = 0; i <= steps; i++)
            {
                var d = top + (bottom - top) * i / steps;
                var (med, lo, hi) = Interpolate(anchors, d, kernel);
                var pt = new AgeCurvePoint($"{modelId}:r{run.RunIndex}:p{i}", d, med, lo, hi, run.RunIndex);
                curve.Add(pt);
                if (med.HasValue) { widthSum += (hi!.Value - lo!.Value); widthN++; }
                if (prev is { } p && p.MedianYrBp.HasValue && med.HasValue)
                {
                    var dd = d - p.DepthM;
                    if (dd > 1e-9)
                    {
                        var slopeMed = (med.Value - p.MedianYrBp.Value) / dd;
                        var slopeFast = (lo!.Value - p.P05YrBp!.Value) / dd;
                        var slopeSlow = (hi!.Value - p.P95YrBp!.Value) / dd;
                        if (slopeFast > 1e-6 && slopeSlow > 1e-6 && slopeMed > 1e-6)
                        {
                            rates.Add(new RateInterval(
                                $"{modelId}:r{run.RunIndex}:p{i - 1}", run.RunIndex, p.DepthM, d,
                                100_000.0 / slopeMed, 100_000.0 / slopeSlow, 100_000.0 / slopeFast, "cm/kyr"));
                        }
                    }
                }
                prev = pt;
            }
        }

        var anchorIds = profile.Anchors.Select(a => a.MaterialId).OrderBy(x => x, StringComparer.Ordinal).ToList();
        if (blockReasons.Count > 0)
        {
            return new CandidateModel(modelId, profile.CoreId, kernel, rules.Version, variant.Key,
                anchorIds, curve, rates, double.PositiveInfinity, SemanticUnsolvable,
                string.Join("; ", blockReasons.Distinct()));
        }
        var score = widthN == 0 ? double.PositiveInfinity : widthSum / widthN;
        return new CandidateModel(modelId, profile.CoreId, kernel, rules.Version, variant.Key,
            anchorIds, curve, rates, score, Ok, null);
    }

    private static List<NormalizedAnchor> AnchorsForRun(NormalizedProfile profile, RunSpan run, TieVariant variant)
    {
        var inRun = profile.Anchors
            .Where(a => a.DepthM >= run.TopM - 1e-9 && a.DepthM <= run.BottomM + 1e-9)
            .OrderBy(a => a.DepthM).ThenBy(a => a.MaterialId, StringComparer.Ordinal)
            .ToList();
        foreach (var pick in variant.Picks)
        {
            if (pick.ChosenAnchor is null) continue;
            var group = profile.TieGroups.First(g => g.Key == pick.TieKey);
            if (inRun.Any(a => group.AnchorIds.Contains(a.MaterialId)))
            {
                inRun = inRun.Where(a => !group.AnchorIds.Contains(a.MaterialId) || a.MaterialId == pick.ChosenAnchor).ToList();
            }
        }
        // intersect pick: collapse the tie group to one synthetic anchor
        foreach (var group in profile.TieGroups.Where(g => g.Resolution == "intersect"))
        {
            var members = inRun.Where(a => group.AnchorIds.Contains(a.MaterialId)).ToList();
            if (members.Count <= 1) continue;
            var chosen = variant.Picks.FirstOrDefault(p => p.TieKey == group.Key)?.ChosenAnchor;
            if (chosen is not null) continue;
            var lo = members.Max(a => a.Age.EffectiveP05);
            var hi = members.Min(a => a.Age.EffectiveP95);
            var med = members.Average(a => a.Age.MedianYrBp);
            var synth = new NormalizedAnchor($"intersect:{group.Key}", group.DepthM,
                new AgeDistribution("normal", med, (hi - lo) / 3.29, lo, hi), members.Average(a => a.ReservoirOffsetYr));
            inRun = inRun.Where(a => !group.AnchorIds.Contains(a.MaterialId)).Append(synth).ToList();
        }
        return inRun.OrderBy(a => a.DepthM).ThenBy(a => a.MaterialId, StringComparer.Ordinal).ToList();
    }

    private static List<string> BlockingDiagnostics(NormalizedProfile profile, RunSpan run)
    {
        var reasons = new List<string>();
        foreach (var d in profile.Diagnostics)
        {
            if (d.Severity != "error") continue;
            var atDepth = profile.Anchors.FirstOrDefault(a => a.MaterialId == d.MaterialId)?.DepthM;
            var inRun = atDepth is { } dd && dd >= run.TopM - 1e-9 && dd <= run.BottomM + 1e-9;
            if (d.Code is "inverted-ages-disjoint" or "tie-unresolvable" && (inRun || d.Code == "tie-unresolvable"))
                reasons.Add(d.Message);
        }
        return reasons;
    }

    private static bool MediansOrdered(List<NormalizedAnchor> anchors)
    {
        for (var i = 1; i < anchors.Count; i++)
            if (anchors[i].Age.MedianYrBp < anchors[i - 1].Age.MedianYrBp - 1.0) return false;
        return true;
    }

    private static (double top, double bottom) AnchorsExtent(NormalizedProfile profile, RunSpan run)
    {
        var ds = profile.Anchors
            .Where(a => a.DepthM >= run.TopM - 1e-9 && a.DepthM <= run.BottomM + 1e-9)
            .Select(a => a.DepthM).ToList();
        return ds.Count == 0 ? (run.TopM, run.BottomM) : (ds.Min(), ds.Max());
    }

    private static (double? med, double? lo, double? hi) Interpolate(List<NormalizedAnchor> anchors, double depth, string kernel)
    {
        if (depth < anchors[0].DepthM - 1e-9 || depth > anchors[^1].DepthM + 1e-9) return (null, null, null);
        if (kernel == "linear")
        {
            return (Quant(anchors, depth, a => a.Age.MedianYrBp),
                    Quant(anchors, depth, a => a.Age.EffectiveP05),
                    Quant(anchors, depth, a => a.Age.EffectiveP95));
        }
        return (MonotoneQuant(anchors, depth, a => a.Age.MedianYrBp),
                Quant(anchors, depth, a => a.Age.EffectiveP05),
                Quant(anchors, depth, a => a.Age.EffectiveP95));
    }

    private static double Quant(List<NormalizedAnchor> anchors, double d, Func<NormalizedAnchor, double> f)
    {
        for (var i = 0; i < anchors.Count - 1; i++)
        {
            if (d <= anchors[i + 1].DepthM + 1e-9)
            {
                var (a, b) = (anchors[i], anchors[i + 1]);
                var t = (d - a.DepthM) / (b.DepthM - a.DepthM);
                return f(a) + t * (f(b) - f(a));
            }
        }
        return f(anchors[^1]);
    }

    // Fritsch-Carlson monotone cubic Hermite interpolation.
    private static double MonotoneQuant(List<NormalizedAnchor> anchors, double d, Func<NormalizedAnchor, double> f)
    {
        var n = anchors.Count;
        var xs = anchors.Select(a => a.DepthM).ToArray();
        var ys = anchors.Select(f).ToArray();
        var delta = new double[n - 1];
        for (var i = 0; i < n - 1; i++) delta[i] = (ys[i + 1] - ys[i]) / (xs[i + 1] - xs[i]);
        var m = new double[n];
        m[0] = delta[0]; m[^1] = delta[^1];
        for (var i = 1; i < n - 1; i++)
            m[i] = delta[i - 1] * delta[i] <= 0 ? 0 : (2 * delta[i - 1] * delta[i]) / (delta[i - 1] + delta[i]);
        var iSeg = Math.Clamp(Array.FindLastIndex(xs, x => x <= d + 1e-9), 0, n - 2);
        if (d >= xs[^1]) return ys[^1];
        var h = xs[iSeg + 1] - xs[iSeg];
        var t = (d - xs[iSeg]) / h;
        var h00 = 2 * t * t * t - 3 * t * t + 1;
        var h10 = t * t * t - 2 * t * t + t;
        var h01 = -2 * t * t * t + 3 * t * t;
        var h11 = t * t * t - t * t;
        return h00 * ys[iSeg] + h10 * h * m[iSeg] + h01 * ys[iSeg + 1] + h11 * h * m[iSeg + 1];
    }

    /// <summary>
    /// Sampling-based band path. Returns three distinct outcomes: ok, resource-exhausted (the
    /// distribution carries more posterior samples than the rule budget) or semantic-unsolvable.
    /// </summary>
    public static SamplingOutcome SampleBand(AgeDistribution dist, RuleSet rules)
    {
        if (dist.SampleCount > rules.MaxSamples)
        {
            return new SamplingOutcome("resource-exhausted",
                $"posterior carries {dist.SampleCount} samples but rule {rules.Version} budgets {rules.MaxSamples}; quantile path remains available",
                null);
        }
        if (dist.SampleCount >= 2)
        {
            var s = AgeDistribution.FromSamples(dist.Samples!);
            return new SamplingOutcome("ok", null, new[] { s.EffectiveP05, s.MedianYrBp, s.EffectiveP95 });
        }
        return new SamplingOutcome("semantic-unsolvable", "distribution has no posterior samples to propagate", null);
    }
}
