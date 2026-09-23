using System;
using System.Collections.Generic;
using System.Linq;
using Chrono.Core.Quantities;

namespace Chrono.Core.Model;

/// <summary>
/// Enumerates defensible age-depth candidates per track and scores them by Monte Carlo.
/// Age monotonicity is required only within one continuous track: every draw is checked for
/// increasing age with increasing depth. A distribution is never reduced to its mean.
/// </summary>
public sealed class CandidateEngine
{
    public DerivationFailure Failure { get; private set; } = DerivationFailure.None;

    public sealed record Strategy(string Name, IReadOnlyList<Anchor> Anchors);

    public List<CandidateModel> Enumerate(Track track, RuleSet rules,
        Dictionary<double, List<string>> duplicateDepth)
    {
        Failure = DerivationFailure.None;
        var candidates = new List<CandidateModel>();
        var anchors = track.Anchors;

        // 1) strategies from conflicting correlation groups: one option per group at a time,
        //    but all combinations are enumerated so every defensible model survives.
        var strategies = BuildStrategies(anchors);

        // 2) tie-depth strategies: co-located anchors with different ages make each single-choice
        //    a distinct candidate; co-located equal ages stay together.
        int enumerated = 0;
        foreach (var st in strategies)
        {
            foreach (var choice in ExpandDuplicateDepths(st.Anchors, duplicateDepth))
            {
                if (enumerated >= rules.MaxCandidates)
                {
                    Failure = DerivationFailure.BudgetExhausted;
                    return candidates;
                }
                candidates.Add(Evaluate(track, st.Name, choice, rules, enumerated));
                enumerated++;
            }
        }
        return candidates;
    }

    private static List<Strategy> BuildStrategies(IReadOnlyList<Anchor> anchors)
    {
        var groups = anchors.Where(a => a.CorrelationGroup != "")
            .GroupBy(a => a.CorrelationGroup).ToList();
        if (groups.Count == 0)
            return new List<Strategy> { new("all_accepted_correlations", anchors) };

        var result = new List<Strategy>();
        var groupOptions = groups.Select(g => g.ToList()).ToList();
        foreach (var combo in Cartesian(groupOptions))
        {
            var chosenIds = new HashSet<string>(combo.Select(a => a.Id));
            var kept = anchors.Where(a => a.CorrelationGroup == "" || chosenIds.Contains(a.Id)).ToList();
            var label = "correlation[" + string.Join("|", combo.OrderBy(a => a.CorrelationGroup).Select(a => a.Id)) + "]";
            result.Add(new Strategy(label, kept));
        }
        return result;
    }

    private static IEnumerable<List<Anchor>> Cartesian(List<List<Anchor>> groups)
    {
        IEnumerable<List<Anchor>> acc = new List<List<Anchor>> { new() };
        foreach (var g in groups)
        {
            var local = g;
            acc = acc.SelectMany(prefix => local.Select(option => {
                var next = new List<Anchor>(prefix) { option };
                return next;
            }));
        }
        // anchors not in any group are appended by caller via chosenIds filtering, so combos here
        // are only the conflicting-group picks.
        return acc.ToList();
    }

    private static IEnumerable<List<Anchor>> ExpandDuplicateDepths(IReadOnlyList<Anchor> anchors,
        Dictionary<double, List<string>> duplicate)
    {
        var byDepth = anchors.GroupBy(a => a.Depth.Cm).ToList();
        var choiceGroups = new List<List<List<Anchor>>>();
        foreach (var g in byDepth)
        {
            if (g.Count() <= 1 || !duplicate.ContainsKey(g.Key))
            {
                choiceGroups.Add(new List<List<Anchor>> { g.ToList() });
                continue;
            }
            // Equal co-located ages: keep together. Different ages: one-anchor candidates plus
            // a combined candidate (which will likely be infeasible — kept for evidence).
            var items = g.ToList();
            var options = new List<List<Anchor>> { items };
            options.AddRange(items.Select(a => new List<Anchor> { a }));
            choiceGroups.Add(options);
        }
        foreach (var combo in CartesianAnchor(choiceGroups))
            yield return combo;
    }

    private static IEnumerable<List<Anchor>> CartesianAnchor(List<List<List<Anchor>>> groups)
    {
        IEnumerable<List<Anchor>> acc = new List<List<Anchor>> { new() };
        foreach (var g in groups)
        {
            var local = g;
            acc = acc.SelectMany(prefix => local.Select(option => {
                var next = new List<Anchor>(prefix);
                next.AddRange(option);
                return next;
            }));
        }
        return acc.Select(list => list.OrderBy(a => a.Depth.Cm).ThenBy(a => a.Id, StringComparer.Ordinal).ToList()).ToList();
    }

    private static CandidateModel Evaluate(Track track, string strategy, IReadOnlyList<Anchor> anchors,
        RuleSet rules, int index)
    {
        var diags = new List<string>();
        bool distinctDepth = anchors.Select(a => a.Depth.Cm).Distinct().Count() == anchors.Count;
        if (!distinctDepth)
            diags.Add("DUPLICATE_DEPTH_WITH_CONFLICTING_AGE: co-located anchors with different ages; draw-wise feasibility evaluated");

        int n = anchors.Count;
        var draws = new double[Math.Max(n, 1)][];
        long feasible = 0;
        // band grid: midpoints between anchors plus anchor positions, within track bounds
        var grid = BuildGrid(track, anchors);
        var ageSamples = grid.Select(_ => new List<double>()).ToArray();
        var rateSamples = new List<double>[Math.Max(0, anchors.Count - 1)];
        for (int i = 0; i < rateSamples.Length; i++) rateSamples[i] = new List<double>();

        long total = n == 0 ? 0 : rules.DrawCount;
        for (int d = 0; d < total; d++)
        {
            // deterministic per-candidate/anchor/draw seeds => equivalent input, identical replay
            var row = new double[n];
            for (int i = 0; i < n; i++)
            {
                ulong seed = rules.RngSeed
                    ^ (ulong)StableHash(track.Id)
                    ^ ((ulong)StableHash(strategy) << 17)
                    ^ ((ulong)StableHash(anchors[i].Id) << 31)
                    ^ ((ulong)d << 47);
                row[i] = anchors[i].EffectiveAge.Draw(new DeterministicRng(seed));
            }

            bool monotone = true;
            for (int i = 1; i < n; i++)
                if (row[i] <= row[i - 1]) { monotone = false; break; }

            bool ratesOk = true;
            for (int i = 1; i < n; i++)
            {
                double depthCm = anchors[i].Depth.Cm - anchors[i - 1].Depth.Cm;
                double dt = row[i] - row[i - 1];
                if (dt <= 0) { ratesOk = false; break; }
                double rate = depthCm / dt;
                if (rate < rules.MinSedRateCmPerYear || rate > rules.MaxSedRateCmPerYear) { ratesOk = false; break; }
            }

            if (monotone && ratesOk && distinctDepth)
            {
                feasible++;
                for (int gi = 0; gi < grid.Count; gi++)
                    ageSamples[gi].Add(Interp(anchors, row, grid[gi]));
                for (int i = 1; i < n; i++)
                {
                    double depthCm = anchors[i].Depth.Cm - anchors[i - 1].Depth.Cm;
                    rateSamples[i - 1].Add(depthCm / (row[i] - row[i - 1]));
                }
            }
        }

        double pMono = total == 0 ? 0 : (double)feasible / total;
        bool hasLongTail = anchors.Any(a =>
            a.EffectiveAge.Kind is "mixture" or "student_t");
        if (hasLongTail)
            diags.Add("LONG_TAIL_DISTRIBUTION_RETAINED: full distribution sampled; no mean substitution");

        var ageBand = BuildAgeBand(track, grid, ageSamples);
        var rates = BuildRateBands(track, anchors, rateSamples);
        bool isFeasible = feasible > 0 && distinctDepth;

        if (n < 2)
        {
            isFeasible = false;
            diags.Add("UNDERCONSTRAINED: fewer than two anchors in a continuous track; no unique age-depth line");
        }
        if (total > 0 && feasible == 0 && distinctDepth && n >= 2)
            diags.Add("SEMANTIC_NO_SOLUTION: no draw satisfies monotone age within continuous segment (inversion/rate envelope)");

        double score = isFeasible ? Score(pMono, ageBand, rules) : double.PositiveInfinity;

        return new CandidateModel(
            Id: $"{track.Id}::cand[{index}]:{StableHash(strategy):x8}",
            TrackId: track.Id,
            AnchorIds: anchors.Select(a => a.Id).ToList(),
            Strategy: strategy,
            Score: score,
            Feasible: isFeasible,
            MonotonicityProbability: pMono,
            FeasibleDraws: feasible,
            TotalDraws: total,
            AgeBand: ageBand,
            RateIntervals: rates,
            Diagnostics: diags);
    }

    private static double Score(double pMono, IReadOnlyList<QuantilePoint> band, RuleSet rules)
    {
        // Lower is better; deterministic, data-only. Ties remain ties — tie-breaking by Id later.
        double width = band.Count == 0 ? double.PositiveInfinity
            : band.Average(p => p.Q975 - p.Q025);
        return width - pMono * 1000.0;
    }

    private static List<Depth> BuildGrid(Track track, IReadOnlyList<Anchor> anchors)
    {
        var depths = new SortedSet<double> { track.Top.Cm, track.Bottom.Cm };
        foreach (var a in anchors) depths.Add(a.Depth.Cm);
        for (int i = 0; i < depths.Count - 1; i++)
        {
            double a = depths.ElementAt(i), b = depths.ElementAt(i + 1);
            depths.Add((a + b) / 2.0);
        }
        return depths.Select(d => new Depth(d)).ToList();
    }

    private static double Interp(IReadOnlyList<Anchor> anchors, double[] ages, Depth at)
    {
        double d = at.Cm;
        if (d <= anchors[0].Depth.Cm) return ages[0];
        for (int i = 1; i < anchors.Count; i++)
        {
            if (d <= anchors[i].Depth.Cm)
            {
                double f = (d - anchors[i - 1].Depth.Cm) /
                           (anchors[i].Depth.Cm - anchors[i - 1].Depth.Cm);
                return ages[i - 1] + f * (ages[i] - ages[i - 1]);
            }
        }
        return ages[^1];
    }

    private static List<QuantilePoint> BuildAgeBand(Track track, List<Depth> grid, List<double>[] samples)
    {
        var band = new List<QuantilePoint>();
        for (int i = 0; i < grid.Count; i++)
        {
            if (samples[i].Count == 0) continue;
            var sorted = samples[i].OrderBy(v => v).ToList();
            var b = DistributionQuantiles.Band(sorted);
            band.Add(new QuantilePoint(track.BandPointId(i), grid[i], b[0], b[1], b[2]));
        }
        return band;
    }

    private static List<QuantilePoint> BuildRateBands(Track track, IReadOnlyList<Anchor> anchors, List<double>[] samples)
    {
        var result = new List<QuantilePoint>();
        for (int i = 0; i < samples.Length; i++)
        {
            if (samples[i].Count == 0) continue;
            var sorted = samples[i].OrderBy(v => v).ToList();
            var b = DistributionQuantiles.Band(sorted);
            double mid = (anchors[i].Depth.Cm + anchors[i + 1].Depth.Cm) / 2.0;
            result.Add(new QuantilePoint(track.RatePointId(i), new Depth(mid), b[0], b[1], b[2]));
        }
        return result;
    }

    /// <summary>FNV-1a 64 over UTF-16 chars; stable across processes/runtimes used here.</summary>
    public static ulong StableHash(string s)
    {
        ulong h = 14695981039346656037UL;
        foreach (char c in s)
        {
            h ^= c;
            h *= 1099511628211UL;
        }
        return h;
    }
}
