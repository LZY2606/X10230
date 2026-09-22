using Chrono.Core.Model;

namespace Chrono.Core.Engine;

public sealed record CandidateBuild(
    CandidateModel? Model,
    long DrawsUsed,
    IReadOnlyList<Diagnostic> Diagnostics);

/// <summary>
/// Produces candidate depth-age models for one run using deterministic Monte Carlo.
/// * linear: anchors joined strictly in depth order (duplicate depths expand into alternates);
/// * isotonic: Pool-Adjacent-Violators guarantees age is non-decreasing *within the run only*.
/// Distributions are sampled per anchor — the mean never replaces the distribution. Ties are broken
/// by stable identity so equivalent inputs keep a stable order.
/// </summary>
public static class CandidateEngine
{
    public static IEnumerable<(string TieKey, List<PreparedAnchor> Choice)> EnumerateChoices(
        IReadOnlyList<PreparedAnchor> anchors)
    {
        var groups = anchors.OrderBy(a => a.DepthMetres)
            .GroupBy(a => Math.Round(a.DepthMetres, 9))
            .OrderBy(g => g.Key)
            .ToList();

        var combos = new List<(string Key, List<PreparedAnchor> Path)> { ("", new List<PreparedAnchor>()) };
        int dupIndex = 0;
        foreach (var g in groups)
        {
            var members = g.OrderBy(a => a.Anchor.Id, StringComparer.Ordinal).ToList();
            var next = new List<(string, List<PreparedAnchor>)>();
            if (members.Count == 1)
            {
                foreach (var (k, path) in combos)
                {
                    var np = new List<PreparedAnchor>(path) { members[0] };
                    next.Add((k, np));
                }
            }
            else
            {
                dupIndex++;
                foreach (var (k, path) in combos)
                {
                    for (int i = 0; i < members.Count; i++)
                    {
                        var np = new List<PreparedAnchor>(path) { members[i] };
                        var key = k + (k.Length == 0 ? "" : "|") + $"d{dupIndex}={members[i].Anchor.Id}";
                        next.Add((key, np));
                    }
                }
            }
            combos = next;
        }
        return combos;
    }

    public static CandidateBuild Build(
        string runId,
        string strategy,
        string tieKey,
        IReadOnlyList<PreparedAnchor> ordered,
        int samples,
        int gridPoints,
        int parametersVersion,
        int materialsVersion,
        long seedBase,
        long budgetRemaining)
    {
        var diags = new List<Diagnostic>();
        int n = ordered.Count;
        long drawsNeeded = (long)samples * n;
        if (drawsNeeded > budgetRemaining)
            return new CandidateBuild(null, 0, new[]
            {
                new Diagnostic("budget.exhausted", Severity.Error, $"run:{runId}",
                    $"candidate needs {drawsNeeded} distribution draws but {budgetRemaining} remain")
            });

        if (n == 0)
            return new CandidateBuild(null, 0, new[]
            {
                new Diagnostic("run.empty", Severity.Error, $"run:{runId}", "run has no usable anchors")
            });

        double d0 = ordered[0].DepthMetres;
        double d1 = ordered[^1].DepthMetres;
        var grid = new double[gridPoints];
        for (int i = 0; i < gridPoints; i++)
            grid[i] = gridPoints == 1 ? d0 : d0 + (d1 - d0) * i / (gridPoints - 1);

        var bandColumns = new double[gridPoints][];
        for (int i = 0; i < gridPoints; i++) bandColumns[i] = new double[samples];

        ulong seed = (ulong)(seedBase + (long)StableString(tieKey));
        for (int s = 0; s < samples; s++)
        {
            var ages = new double[n];
            for (int j = 0; j < n; j++)
            {
                ulong stream = seed + (ulong)s * 1000003UL + (ulong)j * 97UL;
                ages[j] = ordered[j].Calibrated.Sample(stream);
            }
            if (strategy == "isotonic")
                ages = IsotonicNonDecreasing(ages);

            // linear interpolation across the sampled anchors
            for (int gi = 0; gi < gridPoints; gi++)
            {
                double depth = grid[gi];
                bandColumns[gi][s] = Interpolate(ordered, ages, depth);
            }
        }

        var band = new List<AgeBandPoint>(gridPoints);
        for (int gi = 0; gi < gridPoints; gi++)
        {
            var q = QuantileSorted(bandColumns[gi]);
            band.Add(new AgeBandPoint(grid[gi], q.q05, q.median, q.q95));
        }

        var rates = new List<RateBandPoint>();
        for (int gi = 0; gi < gridPoints - 1; gi++)
        {
            double depthSpanM = grid[gi + 1] - grid[gi];
            var low = new double[samples];
            for (int s = 0; s < samples; s++)
            {
                double dKa = bandColumns[gi + 1][s] - bandColumns[gi][s];
                // cm per kyr; downward depth means older (positive) age change. Negative rates
                // (inversion in the raw linear candidate) are preserved rather than hidden.
                double denom = Math.Abs(dKa) < 1e-12 ? 1e-9 * (dKa >= 0 ? 1 : -1) : dKa;
                low[s] = depthSpanM * 100.0 / denom;
            }
            var r = QuantileSorted(low);
            rates.Add(new RateBandPoint(grid[gi], grid[gi + 1], r.q05, r.median, r.q95));
        }

        string id = CandidateId(runId, strategy, tieKey, ordered, parametersVersion, materialsVersion);
        double score = Score(band, ordered);
        var inputSummary = string.Join(";", ordered.Select(p =>
            $"{p.Anchor.Id}@{p.DepthMetres:R}:{p.Calibrated.Kind}"));

        var model = new CandidateModel(id, runId, strategy, tieKey, score, band, rates,
            ordered.Select(p => p.Anchor.Id).ToList(), false, inputSummary, parametersVersion, materialsVersion);
        return new CandidateBuild(model, drawsNeeded, diags);
    }

    public static string CandidateId(string runId, string strategy, string tieKey,
        IReadOnlyList<PreparedAnchor> ordered, int pv, int mv)
    {
        var canonical = string.Join("|", ordered.Select(p => $"{p.Anchor.Id}@{p.DepthMetres:R}:{p.Calibrated.Kind}"))
            + $"#s={strategy}#t={tieKey}#p{pv}#m{mv}";
        return "cand_" + StableId.Sha1("cand", runId + "|" + canonical)[..20];
    }

    private static double Interpolate(IReadOnlyList<PreparedAnchor> anchors, double[] ages, double depth)
    {
        if (depth <= anchors[0].DepthMetres) return ages[0];
        if (depth >= anchors[^1].DepthMetres) return ages[^1];
        for (int j = 0; j < anchors.Count - 1; j++)
        {
            double a = anchors[j].DepthMetres, b = anchors[j + 1].DepthMetres;
            if (depth >= a && depth <= b)
            {
                double f = b <= a ? 0 : (depth - a) / (b - a);
                return ages[j] + (ages[j + 1] - ages[j]) * f;
            }
        }
        return ages[^1];
    }

    /// <summary>Pool-Adjacent-Violators for non-decreasing sequence (ages must not get younger down-core).</summary>
    public static double[] IsotonicNonDecreasing(double[] x)
    {
        var blocks = new List<(double Mean, int Weight)>();
        foreach (var v in x)
        {
            blocks.Add((v, 1));
            while (blocks.Count >= 2 && blocks[^2].Mean > blocks[^1].Mean + 1e-12)
            {
                var (m1, w1) = blocks[^2];
                var (m2, w2) = blocks[^1];
                blocks.RemoveRange(blocks.Count - 2, 2);
                blocks.Add(((m1 * w1 + m2 * w2) / (w1 + w2), w1 + w2));
            }
        }
        var result = new double[x.Length];
        int idx = 0;
        foreach (var (mean, weight) in blocks)
            for (int k = 0; k < weight; k++) result[idx++] = mean;
        return result;
    }

    /// <summary>Tighter median band against anchors, with inversion penalty — lower is better.</summary>
    private static double Score(IReadOnlyList<AgeBandPoint> band, IReadOnlyList<PreparedAnchor> anchors)
    {
        double width = 0;
        foreach (var p in band) width += (p.Q95 - p.Q05);
        width /= band.Count;
        return width;
    }

    public static (double q05, double median, double q95) QuantileSorted(double[] values)
    {
        Array.Sort(values);
        double Q(double p)
        {
            double pos = p * (values.Length - 1);
            int lo = (int)Math.Floor(pos), hi = (int)Math.Ceiling(pos);
            if (lo == hi) return values[lo];
            return values[lo] + (values[hi] - values[lo]) * (pos - lo);
        }
        return (Q(0.05), Q(0.5), Q(0.95));
    }

    private static ulong StableString(string s)
    {
        ulong h = 1469598103934665603UL;
        foreach (var c in s) { h ^= c; h *= 1099511628211UL; }
        return h;
    }
}
