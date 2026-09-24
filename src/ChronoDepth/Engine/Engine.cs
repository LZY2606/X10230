using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using ChronoDepth.Domain;

namespace ChronoDepth.Engine;

/// <summary>Versioned engine parameters. Any change bumps <see cref="Version"/>.</summary>
public sealed record EngineParams(
    string Version,
    double DepthEpsilonM,
    double MonotonicToleranceKa,
    int McDraws,
    int MaxCandidates,
    int MaxCombinations)
{
    public static readonly EngineParams V1 = new("v1", 0.005, 0.05, 400, 32, 256);
    public static readonly EngineParams V2 = new("v2", 0.005, 0.10, 800, 32, 256);
}

public sealed record DepthPoint(double DepthM, double AgeKa);

public sealed record RateInterval(
    double TopM, double BottomM,
    double MedianKaPerM, double P5KaPerM, double P95KaPerM, string Validity);

public enum CandidateStatus { Adopted, Rejected }

public sealed record CandidateModel(
    string Id, string RunId, CandidateStatus Status, string? RejectReason,
    IReadOnlyList<string> UsedAnchorIds,
    IReadOnlyList<DepthPoint> MedianPath,
    IReadOnlyList<DepthPoint> BandP5,
    IReadOnlyList<DepthPoint> BandP95,
    IReadOnlyList<RateInterval> Rates,
    string InputDigest, string ParamVersion,
    IReadOnlyList<string> ParentModelIds,
    IReadOnlyList<Diagnostic> Diagnostics);

/// <summary>Three failure modes are distinct results, never conflated.</summary>
public abstract record EngineResult
{
    public sealed record Success(
        IReadOnlyList<CandidateModel> Models,
        IReadOnlyList<Diagnostic> Diagnostics) : EngineResult;

    public sealed record BudgetExhausted(
        IReadOnlyList<CandidateModel> Partial,
        IReadOnlyList<Diagnostic> Diagnostics) : EngineResult;

    public sealed record CorruptFormat(
        IReadOnlyList<Diagnostic> Diagnostics) : EngineResult;

    public sealed record Unsolvable(
        string Reason,
        IReadOnlyList<Diagnostic> Diagnostics) : EngineResult;
}

internal sealed record Run(string Id, List<Segment> Segments, List<Anchor> Anchors)
{
    public string Digest()
    {
        var payload = string.Join('|', Segments.Select(s => $"{s.Id}:{s.TopM}:{s.BottomM}"))
            + "#" + string.Join('|', Anchors.Select(a =>
                $"{a.Id}:{a.Depth.Raw}:{a.Depth.Unit}:{a.Depth.Datum}:{a.ReservoirOffsetYr}:" +
                string.Join(',', a.Age.Samples.Select(x => x.ToString("G17")))));
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(payload)))[..16];
    }
}

public static class DepthAgeEngine
{
    /// <summary>
    /// Compute candidate depth-age models. Age is monotonic only inside
    /// unbroken runs; gaps and unconformities are explicit run boundaries.
    /// Anchor distributions are sampled, never replaced by their mean.
    /// </summary>
    public static EngineResult Compute(
        MaterialSet materials,
        IReadOnlySet<string> excludedAnchorIds,
        EngineParams p,
        IReadOnlyDictionary<string, List<string>>? parentMap = null)
    {
        var diags = new List<Diagnostic>();

        // --- Corrupt format: unparseable quantities are located errors ---
        var corrupt = false;
        foreach (var a in materials.Anchors)
        {
            if (a.Depth.Value is null)
            {
                diags.Add(new Diagnostic("corrupt-quantity",
                    $"Depth '{a.Depth.Raw}' cannot be parsed as {a.Depth.Unit}",
                    $"anchor/{a.Id}/depth", DiagSeverity.Error));
                corrupt = true;
            }
            if (a.Age.Samples.Length == 0)
            {
                diags.Add(new Diagnostic("corrupt-distribution",
                    "Age distribution has no samples", $"anchor/{a.Id}/age", DiagSeverity.Error));
                corrupt = true;
            }
        }
        foreach (var s in materials.Segments)
        {
            if (s.BottomM <= s.TopM)
            {
                diags.Add(new Diagnostic("corrupt-segment",
                    $"Segment bottom {s.BottomM} <= top {s.TopM}", $"segment/{s.Id}", DiagSeverity.Error));
                corrupt = true;
            }
        }
        if (corrupt) return new EngineResult.CorruptFormat(diags);

        var runs = BuildRuns(materials, excludedAnchorIds, diags);
        var models = new List<CandidateModel>();
        var budgetHit = false;
        var solvableRuns = 0;

        foreach (var run in runs)
        {
            var runModels = ComputeRun(run, p, diags, ref budgetHit, parentMap);
            if (runModels.Any(m => m.Status == CandidateStatus.Adopted)) solvableRuns++;
            models.AddRange(runModels);
            if (budgetHit) break;
        }

        // Stable ordering for equivalent inputs: digest of choices, then id.
        models = models
            .OrderBy(m => m.RunId, StringComparer.Ordinal)
            .ThenBy(m => m.Status)
            .ThenBy(m => string.Join(',', m.UsedAnchorIds), StringComparer.Ordinal)
            .ThenBy(m => m.Id, StringComparer.Ordinal)
            .ToList();

        if (budgetHit)
            return new EngineResult.BudgetExhausted(models, diags);
        if (runs.Count > 0 && solvableRuns == 0)
            return new EngineResult.Unsolvable(
                "No monotonic depth-age path exists in any unbroken run", diags);
        return new EngineResult.Success(models, diags);
    }

    /// <summary>Digest of the input feeding one run; used for local recompute.</summary>
    public static IReadOnlyDictionary<string, string> RunDigests(
        MaterialSet materials, IReadOnlySet<string> excludedAnchorIds)
    {
        var diags = new List<Diagnostic>();
        return BuildRuns(materials, excludedAnchorIds, diags)
            .ToDictionary(r => r.Id, r => r.Digest());
    }

    private static List<Run> BuildRuns(
        MaterialSet m, IReadOnlySet<string> excluded, List<Diagnostic> diags)
    {
        var segments = m.Segments.OrderBy(s => s.TopM).ThenBy(s => s.Id, StringComparer.Ordinal).ToList();
        var boundaryAfter = m.Boundaries.ToDictionary(b => b.AfterSegmentId, b => b);
        var runs = new List<Run>();
        var current = new List<Segment>();
        var runIndex = 0;

        void Flush()
        {
            if (current.Count == 0) return;
            var segIds = current.Select(s => s.Id).ToHashSet(StringComparer.Ordinal);
            var anchors = m.Anchors
                .Where(a => segIds.Contains(a.SegmentId) && !excluded.Contains(a.Id))
                .OrderBy(a => a.Depth.Value!.Value)
                .ThenBy(a => a.Id, StringComparer.Ordinal)
                .ToList();
            runs.Add(new Run($"run-{runIndex}:{string.Join('+', current.Select(s => s.Id))}",
                new List<Segment>(current), anchors));
            runIndex++;
            current.Clear();
        }

        foreach (var seg in segments)
        {
            current.Add(seg);
            if (boundaryAfter.TryGetValue(seg.Id, out var b) && b.Kind != BoundaryKind.Continuous)
            {
                diags.Add(new Diagnostic("explicit-boundary",
                    $"{b.Kind} boundary after segment {seg.Id}" +
                    (b.GapTopM is not null ? $" (gap {b.GapTopM}-{b.GapBottomM} m)" : ""),
                    $"boundary/{b.Id}", DiagSeverity.Info));
                Flush();
            }
        }
        Flush();
        return runs;
    }

    private static List<CandidateModel> ComputeRun(
        Run run, EngineParams p, List<Diagnostic> diags, ref bool budgetHit,
        IReadOnlyDictionary<string, List<string>>? parentMap)
    {
        var result = new List<CandidateModel>();
        var runDigest = run.Digest();
        if (run.Anchors.Count == 0)
        {
            diags.Add(new Diagnostic("run-without-anchors",
                $"Run {run.Id} has no included anchors", $"run/{run.Id}", DiagSeverity.Warning));
            return result;
        }

        foreach (var a in run.Anchors.Where(a => a.Age.IsLongTail))
            diags.Add(new Diagnostic("age-dist-long-tail",
                $"Anchor {a.Id} age distribution has a long upper tail; quantiles kept, not averaged",
                $"anchor/{a.Id}/age", DiagSeverity.Info));

        // Group anchors by depth (depth duplication / competing anchors).
        var groups = new List<List<Anchor>>();
        foreach (var a in run.Anchors)
        {
            if (groups.Count > 0 &&
                Math.Abs(groups[^1][0].Depth.Value!.Value - a.Depth.Value!.Value) <= p.DepthEpsilonM)
                groups[^1].Add(a);
            else
                groups.Add(new List<Anchor> { a });
        }

        var choiceSets = new List<List<Anchor>>();
        foreach (var g in groups)
        {
            var distinctMedians = g.Select(a => Math.Round(a.Age.Median - a.ReservoirOffsetYr / 1000.0, 6))
                .Distinct().Count();
            if (g.Count > 1 && distinctMedians <= 1)
            {
                diags.Add(new Diagnostic("depth-duplicate",
                    $"{g.Count} anchors share depth {g[0].Depth.Raw} {g[0].Depth.Unit} with equal ages; first kept",
                    $"run/{run.Id}", DiagSeverity.Warning));
                choiceSets.Add(new List<Anchor> { g[0] });
            }
            else if (g.Count > 1)
            {
                diags.Add(new Diagnostic("competing-anchors",
                    $"{g.Count} competing anchors at depth {g[0].Depth.Raw} {g[0].Depth.Unit}; one candidate per choice",
                    $"run/{run.Id}", DiagSeverity.Info));
                choiceSets.Add(g);
            }
            else choiceSets.Add(g);
        }

        // Enumerate combinations (one anchor per depth group), budget-capped.
        var combos = new List<List<Anchor>> { new() };
        foreach (var set in choiceSets)
        {
            var next = new List<List<Anchor>>();
            foreach (var combo in combos)
            foreach (var anchor in set)
            {
                if (next.Count >= p.MaxCombinations) { budgetHit = true; break; }
                var c = new List<Anchor>(combo) { anchor };
                next.Add(c);
            }
            if (budgetHit) break;
            combos = next;
        }

        var adopted = 0;
        foreach (var combo in combos)
        {
            if (adopted >= p.MaxCandidates) { budgetHit = true; break; }
            var chosen = combo.OrderBy(a => a.Depth.Value!.Value)
                .ThenBy(a => a.Id, StringComparer.Ordinal).ToList();
            var model = BuildCandidate(run, chosen, runDigest, p, parentMap);
            if (model.Status == CandidateStatus.Adopted) adopted++;
            result.Add(model);
        }
        return result;
    }

    private static CandidateModel BuildCandidate(
        Run run, List<Anchor> chosen, string runDigest, EngineParams p,
        IReadOnlyDictionary<string, List<string>>? parentMap)
    {
        var diags = new List<Diagnostic>();
        double Corrected(Anchor a, double ageKa) => ageKa - a.ReservoirOffsetYr / 1000.0;

        // Monotonicity: age must not decrease with depth inside a run.
        var inverted = false;
        for (var i = 1; i < chosen.Count; i++)
        {
            var prev = Corrected(chosen[i - 1], chosen[i - 1].Age.Median);
            var cur = Corrected(chosen[i], chosen[i].Age.Median);
            if (cur < prev - p.MonotonicToleranceKa)
            {
                diags.Add(new Diagnostic("inverted-section",
                    $"Age decreases with depth between {chosen[i - 1].Id} ({prev:F3} ka) and {chosen[i].Id} ({cur:F3} ka)",
                    $"run/{run.Id}", DiagSeverity.Error));
                inverted = true;
            }
        }

        var usedIds = chosen.Select(a => a.Id).ToList();
        var idPayload = $"{run.Id}|{string.Join(',', usedIds)}|{p.Version}|{runDigest}";
        var modelId = "mdl-" + Convert.ToHexString(
            SHA256.HashData(Encoding.UTF8.GetBytes(idPayload)))[..12];
        if (parentMap is null || !parentMap.TryGetValue(run.Id, out var parents))
            parents = new List<string>();

        if (inverted)
        {
            return new CandidateModel(modelId, run.Id, CandidateStatus.Rejected,
                "inverted-section", usedIds,
                chosen.Select(a => new DepthPoint(a.Depth.Value!.Value, Corrected(a, a.Age.Median))).ToList(),
                Array.Empty<DepthPoint>(), Array.Empty<DepthPoint>(), Array.Empty<RateInterval>(),
                runDigest, p.Version, parents, diags);
        }

        // Monte Carlo over full distributions; monotonicity via cumulative max.
        var seed = BitConverter.ToInt32(SHA256.HashData(Encoding.UTF8.GetBytes(modelId)));
        var rng = new Random(seed);
        var depths = chosen.Select(a => a.Depth.Value!.Value).ToArray();
        var draws = new double[p.McDraws][];
        for (var d = 0; d < p.McDraws; d++)
        {
            var row = new double[chosen.Count];
            for (var i = 0; i < chosen.Count; i++)
            {
                var s = chosen[i].Age.Samples;
                row[i] = Corrected(chosen[i], s[rng.Next(s.Length)]);
                if (i > 0 && row[i] < row[i - 1]) row[i] = row[i - 1];
            }
            draws[d] = row;
        }

        double QuantileAt(int idx, double q)
        {
            var col = draws.Select(r => r[idx]).OrderBy(x => x).ToArray();
            var pos = q * (col.Length - 1);
            var lo = (int)Math.Floor(pos);
            var hi = Math.Min(lo + 1, col.Length - 1);
            return col[lo] + (col[hi] - col[lo]) * (pos - lo);
        }

        var median = chosen.Select((a, i) => new DepthPoint(depths[i], Corrected(a, a.Age.Median))).ToList();
        var bandLo = depths.Select((d, i) => new DepthPoint(d, QuantileAt(i, 0.05))).ToList();
        var bandHi = depths.Select((d, i) => new DepthPoint(d, QuantileAt(i, 0.95))).ToList();

        var rates = new List<RateInterval>();
        for (var i = 1; i < chosen.Count; i++)
        {
            var dz = depths[i] - depths[i - 1];
            if (dz <= 0) continue;
            var samples = draws.Select(r => (r[i] - r[i - 1]) / dz).OrderBy(x => x).ToArray();
            double Q(double q)
            {
                var pos = q * (samples.Length - 1);
                var lo = (int)Math.Floor(pos);
                var hi = Math.Min(lo + 1, samples.Length - 1);
                return samples[lo] + (samples[hi] - samples[lo]) * (pos - lo);
            }
            rates.Add(new RateInterval(depths[i - 1], depths[i], Q(0.5), Q(0.05), Q(0.95),
                "within-run; boundary-aware"));
        }

        return new CandidateModel(modelId, run.Id, CandidateStatus.Adopted, null,
            usedIds, median, bandLo, bandHi, rates, runDigest, p.Version, parents, diags);
    }
}
