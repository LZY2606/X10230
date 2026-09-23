using System.Text.Json.Nodes;

namespace Chronostrat.Core;

/// <summary>Versioned derivation parameters; every derived result records this version.</summary>
public sealed record DeriveParameters(
    int Version,
    int McBudget,              // total Monte-Carlo draws per candidate
    int MinValidDraws,         // minimum valid draws to accept a candidate
    double EquivalenceTolerance, // support ratio within which candidates are equally defensible
    int Seed)
{
    public static DeriveParameters Default => new(1, 4000, 200, 0.05, 42);
}

/// <summary>Distinct outcomes — budget, format and semantic failures are never conflated.</summary>
public abstract record DeriveOutcome
{
    public sealed record Derived(ModelSet Models) : DeriveOutcome;
    public sealed record BudgetExhausted(string Position, int Spent, int Needed) : DeriveOutcome;
    public sealed record SemanticUnsolvable(string Position, string Reason) : DeriveOutcome;
}

public sealed record RateInterval(
    string SegmentId,
    double TopDepth,
    double BaseDepth,
    Quantity MedianRate,
    Quantity P05,
    Quantity P95);

public sealed record UncertaintyPoint(
    double Depth,
    double P05,
    double P50,
    double P95,
    string AgeUnit);

public sealed record SegmentModel(
    string SegmentId,
    IReadOnlyList<RateInterval> Rates,
    IReadOnlyList<UncertaintyPoint> Band,
    int ValidDraws,
    int TotalDraws);

public sealed record CandidateModel(
    string Id,
    string Label,
    IReadOnlyList<SegmentModel> Segments,
    string[] AnchorIds,
    double Support,
    string Status,            // "retained" | "inferior" — equally defensible ones are all retained
    IReadOnlyList<Diagnostic> Diagnostics);

public sealed record ModelSet(
    string Id,
    IReadOnlyList<CandidateModel> Candidates,
    IReadOnlyList<Diagnostic> Diagnostics,
    IReadOnlyList<JsonObject> Verdicts);

/// <summary>
/// The depth-age derivation engine. Rules:
///  - age is monotonic only inside continuous segments; gaps and unconformities
///    are explicit boundaries across which no monotonicity is enforced;
///  - an anchor's full distribution is sampled, never replaced by its mean;
///  - equally defensible candidates are all retained, in stable order.
/// </summary>
public static class DepthAgeEngine
{
    private const double DepthEpsilon = 1e-9;
    private const int MaxCombinations = 8;

    public static DeriveOutcome Derive(MaterialsSnapshot materials, DeriveParameters pars)
    {
        var diagnostics = new List<Diagnostic>(materials.ImportDiagnostics);
        var verdicts = new List<JsonObject>();

        if (materials.Datum is null)
        {
            return new DeriveOutcome.SemanticUnsolvable(
                "datum",
                "no depth datum declared; cannot place anchors on a coordinate basis");
        }

        var offsets = materials.ReservoirOffsets.ToDictionary(o => o.Id, o => o.Offset);
        var candidates = new List<CandidateModel>();
        bool anyBudgetFailure = false;
        string? budgetPosition = null;
        int budgetSpent = 0;

        foreach (var segment in materials.Segments.OrderBy(s => s.TopDepth))
        {
            var anchors = materials.ActiveAnchors()
                .Where(a => a.DatumId == materials.Datum.Id)
                .Where(a => InSegment(a, segment))
                .ToArray();

            // Anchors whose depth unit cannot be normalized stay in place with a diagnostic.
            var usable = new List<(AgeAnchor Anchor, double DepthM)>();
            foreach (var a in anchors)
            {
                if (UnitRegistry.TryNormalizeDepth(new Quantity(a.Depth, a.DepthUnit), out var nd))
                    usable.Add((a, nd.Value));
                else
                    diagnostics.Add(new Diagnostic(
                        "unconvertible-depth-unit",
                        $"depth unit '{a.DepthUnit}' cannot be normalized; anchor kept raw, not plotted",
                        $"anchor:{a.Id}@depth",
                        "error"));
            }

            if (usable.Count < 2)
            {
                diagnostics.Add(new Diagnostic(
                    "insufficient-anchors",
                    $"segment {segment.Id} has {usable.Count} usable anchor(s); at least 2 required",
                    $"segment:{segment.Id}",
                    "warning"));
                continue;
            }

            // Depth duplicates -> tied anchors -> one alternative per anchor.
            var byDepth = usable
                .GroupBy(x => x.DepthM)
                .OrderBy(g => g.Key)
                .Select(g => g.OrderBy(x => x.Anchor.Id, StringComparer.Ordinal).ToArray())
                .ToArray();

            var combinations = EnumerateCombinations(byDepth).Take(MaxCombinations).ToList();
            if (byDepth.Any(g => g.Length > 1))
            {
                verdicts.Add(Verdict(RuleIds.MonotonicWithinContinuousSegment, segment.Id, "tied-anchors",
                    new JsonObject
                    {
                        ["depths"] = new JsonArray(byDepth.Where(g => g.Length > 1).Select(g => JsonValue.Create(g.Key)).ToArray()),
                        ["alternatives"] = combinations.Count
                    }));
            }

            foreach (var combo in combinations)
            {
                // Split inverted sequences into maximal monotonic subsets (by median age).
                foreach (var subset in MonotonicSubsets(combo))
                {
                    if (subset.Count < 2) continue;
                    var built = BuildCandidate(materials, segment, subset, offsets, pars, verdicts,
                        out bool budgetFailed, out int spent);
                    if (budgetFailed)
                    {
                        anyBudgetFailure = true;
                        budgetPosition = $"segment:{segment.Id}";
                        budgetSpent = spent;
                        continue;
                    }
                    if (built is not null) candidates.Add(built);
                }
            }
        }

        if (candidates.Count == 0)
        {
            if (anyBudgetFailure)
                return new DeriveOutcome.BudgetExhausted(budgetPosition ?? "segment:?", budgetSpent, pars.MinValidDraws);
            return new DeriveOutcome.SemanticUnsolvable(
                "model",
                "no continuous segment admits a monotonic depth-age assignment");
        }

        // Equally defensible candidates are all retained; stable order by content hash.
        double maxSupport = candidates.Max(c => c.Support);
        var final = candidates
            .Select(c => c with
            {
                Status = c.Support >= maxSupport * (1.0 - pars.EquivalenceTolerance) ? "retained" : "inferior"
            })
            .OrderBy(c => c.Id, StringComparer.Ordinal)
            .ToArray();

        verdicts.Add(Verdict(RuleIds.EquallyDefensibleModelsRetained, "model-set", "retained",
            new JsonObject
            {
                ["retained"] = new JsonArray(final.Where(c => c.Status == "retained").Select(c => JsonValue.Create(c.Id)).ToArray()),
                ["inferior"] = new JsonArray(final.Where(c => c.Status == "inferior").Select(c => JsonValue.Create(c.Id)).ToArray())
            }));

        var setPayload = new JsonObject
        {
            ["candidates"] = new JsonArray(final.Select(c => JsonValue.Create(c.Id)).ToArray()),
            ["paramVersion"] = pars.Version
        };
        var modelSet = new ModelSet(StableId.For("modelset", setPayload), final, diagnostics, verdicts);
        return new DeriveOutcome.Derived(modelSet);
    }

    private static bool InSegment(AgeAnchor a, Segment s)
    {
        if (!UnitRegistry.TryNormalizeDepth(new Quantity(a.Depth, a.DepthUnit), out var nd)) return false;
        if (!UnitRegistry.TryNormalizeDepth(new Quantity(s.TopDepth, s.Unit), out var top)) return false;
        if (!UnitRegistry.TryNormalizeDepth(new Quantity(s.BaseDepth, s.Unit), out var bse)) return false;
        return nd.Value >= top.Value - DepthEpsilon && nd.Value <= bse.Value + DepthEpsilon;
    }

    private static IEnumerable<(AgeAnchor Anchor, double DepthM)[]> EnumerateCombinations(
        (AgeAnchor Anchor, double DepthM)[][] byDepth)
    {
        var acc = new List<(AgeAnchor, double)>();
        var results = new List<(AgeAnchor Anchor, double DepthM)[]>();
        void Rec(int i)
        {
            if (results.Count >= MaxCombinations) return;
            if (i == byDepth.Length)
            {
                results.Add(acc.Select(x => (x.Item1, x.Item2)).ToArray());
                return;
            }
            foreach (var option in byDepth[i])
            {
                acc.Add(option);
                Rec(i + 1);
                acc.RemoveAt(acc.Count - 1);
            }
        }
        Rec(0);
        foreach (var r in results) yield return r;
    }

    /// <summary>Maximal strictly-increasing (by median age) subsequences, depth order preserved.</summary>
    private static List<List<(AgeAnchor Anchor, double DepthM)>> MonotonicSubsets(
        (AgeAnchor Anchor, double DepthM)[] combo)
    {
        var ordered = combo.OrderBy(x => x.DepthM).ToArray();
        bool IsMonotonic(List<(AgeAnchor Anchor, double DepthM)> xs)
        {
            for (int i = 1; i < xs.Count; i++)
                if (xs[i].Anchor.Age.Median() <= xs[i - 1].Anchor.Age.Median()) return false;
            return true;
        }

        var full = ordered.ToList();
        if (IsMonotonic(full)) return new List<List<(AgeAnchor, double)>> { full };

        // Inverted section: enumerate maximal monotonic subsets (drop minimal offenders).
        var results = new List<List<(AgeAnchor Anchor, double DepthM)>>();
        void Rec(List<(AgeAnchor Anchor, double DepthM)> xs)
        {
            if (xs.Count < 2) return;
            if (IsMonotonic(xs))
            {
                // keep only maximal ones
                if (!results.Any(r => xs.All(x => r.Contains(x)))) results.Add(xs);
                return;
            }
            for (int i = 1; i < xs.Count; i++)
            {
                if (xs[i].Anchor.Age.Median() <= xs[i - 1].Anchor.Age.Median())
                {
                    var dropLeft = xs.Where((_, j) => j != i - 1).ToList();
                    var dropRight = xs.Where((_, j) => j != i).ToList();
                    Rec(dropLeft);
                    Rec(dropRight);
                    return;
                }
            }
        }
        Rec(full);
        // dedupe by anchor-id signature
        return results
            .GroupBy(r => string.Join(",", r.Select(x => x.Anchor.Id)))
            .Select(g => g.First())
            .Take(MaxCombinations)
            .ToList();
    }

    private static CandidateModel? BuildCandidate(
        MaterialsSnapshot materials,
        Segment segment,
        List<(AgeAnchor Anchor, double DepthM)> subset,
        Dictionary<string, AgeDistribution> offsets,
        DeriveParameters pars,
        List<JsonObject> verdicts,
        out bool budgetFailed,
        out int spent)
    {
        budgetFailed = false;
        spent = 0;

        // Analytic feasibility: does a strictly increasing assignment exist within supports?
        if (!Feasible(subset, offsets))
        {
            verdicts.Add(Verdict(RuleIds.MonotonicWithinContinuousSegment, segment.Id, "violated",
                new JsonObject
                {
                    ["anchors"] = new JsonArray(subset.Select(s => JsonValue.Create(s.Anchor.Id)).ToArray()),
                    ["reason"] = "no strictly increasing age assignment exists within the declared supports"
                }));
            return null; // semantic dead end for this subset; others may survive
        }

        var rng = new Random(HashSeed(pars.Seed, subset));
        int valid = 0;
        int total = 0;
        var curves = new List<double[]>();
        var depths = subset.Select(s => s.DepthM).ToArray();

        while (total < pars.McBudget && valid < Math.Max(pars.MinValidDraws, 400))
        {
            total++;
            var ages = new double[subset.Count];
            for (int i = 0; i < subset.Count; i++)
            {
                double age = subset[i].Anchor.Age.Draw(rng);
                if (subset[i].Anchor.ReservoirOffsetId is { } offId && offsets.TryGetValue(offId, out var off))
                    age -= off.Draw(rng);
                ages[i] = age;
            }
            bool ok = true;
            for (int i = 1; i < ages.Length; i++)
                if (ages[i] <= ages[i - 1]) { ok = false; break; }
            if (!ok) continue;
            valid++;
            curves.Add(ages);
        }
        spent = total;

        if (valid < pars.MinValidDraws)
        {
            budgetFailed = true;
            return null;
        }

        // Uncertainty band on a depth grid — full distributions, never the mean.
        const int grid = 25;
        double top = depths.First(), bse = depths.Last();
        var band = new List<UncertaintyPoint>();
        for (int gIdx = 0; gIdx <= grid; gIdx++)
        {
            double d = top + (bse - top) * gIdx / grid;
            var samples = curves.Select(c => Interp(depths, c, d)).OrderBy(x => x).ToArray();
            band.Add(new UncertaintyPoint(d, Quantile(samples, 0.05), Quantile(samples, 0.50), Quantile(samples, 0.95),
                subset[0].Anchor.Age.Unit));
        }

        // Sedimentation rates per interval (cm/ka).
        var rates = new List<RateInterval>();
        for (int i = 1; i < depths.Length; i++)
        {
            var rs = curves
                .Select(c => (depths[i] - depths[i - 1]) * 100.0 / (c[i] - c[i - 1]))
                .OrderBy(x => x).ToArray();
            rates.Add(new RateInterval(
                segment.Id, depths[i - 1], depths[i],
                new Quantity(Quantile(rs, 0.50), "cm/ka"),
                new Quantity(Quantile(rs, 0.05), "cm/ka"),
                new Quantity(Quantile(rs, 0.95), "cm/ka")));
        }

        var segModel = new SegmentModel(segment.Id, rates, band, valid, total);
        var anchorIds = subset.Select(s => s.Anchor.Id).ToArray();
        var candPayload = new JsonObject
        {
            ["segment"] = segment.Id,
            ["anchors"] = new JsonArray(anchorIds.Select(JsonValue.Create).ToArray()),
            ["paramVersion"] = pars.Version
        };
        string candId = StableId.For("cand", candPayload);

        verdicts.Add(Verdict(RuleIds.DistributionNotReplacedByMean, segment.Id, "satisfied",
            new JsonObject
            {
                ["anchors"] = new JsonArray(subset.Select(s => (JsonNode)JsonValue.Create(s.Anchor.Id)).ToArray()),
                ["draws"] = valid,
                ["note"] = "candidates sampled from full distributions; no mean substitution"
            }));

        return new CandidateModel(
            candId,
            $"{segment.Id}[{string.Join(",", anchorIds)}]",
            new[] { segModel },
            anchorIds,
            (double)valid / total,
            "retained",
            Array.Empty<Diagnostic>());
    }

    private static bool Feasible(
        List<(AgeAnchor Anchor, double DepthM)> subset,
        Dictionary<string, AgeDistribution> offsets)
    {
        // Support bounds; normal => unbounded (always feasible).
        double Lo(AgeDistribution d) => d.Kind switch
        {
            "uniform" => d.Min!.Value,
            "empirical" => d.Samples!.Min(),
            _ => double.NegativeInfinity
        };
        double Hi(AgeDistribution d) => d.Kind switch
        {
            "uniform" => d.Max!.Value,
            "empirical" => d.Samples!.Max(),
            _ => double.PositiveInfinity
        };

        double runningLo = double.NegativeInfinity;
        foreach (var (anchor, _) in subset)
        {
            double lo = Lo(anchor.Age), hi = Hi(anchor.Age);
            if (anchor.ReservoirOffsetId is { } offId && offsets.TryGetValue(offId, out var off))
            {
                lo -= Hi(off);
                hi -= Lo(off);
            }
            double newLo = Math.Max(lo, runningLo);
            if (newLo >= hi) return false; // no room for a strictly larger draw
            runningLo = newLo + 1e-12;
        }
        return true;
    }

    private static int HashSeed(int seed, List<(AgeAnchor Anchor, double DepthM)> subset)
    {
        unchecked
        {
            int h = seed;
            foreach (var (a, _) in subset)
                h = h * 31 + a.Id.GetHashCode();
            return h;
        }
    }

    private static double Interp(double[] xs, double[] ys, double x)
    {
        if (x <= xs[0]) return ys[0];
        if (x >= xs[^1]) return ys[^1];
        for (int i = 1; i < xs.Length; i++)
        {
            if (x <= xs[i])
            {
                double t = (x - xs[i - 1]) / (xs[i] - xs[i - 1]);
                return ys[i - 1] + t * (ys[i] - ys[i - 1]);
            }
        }
        return ys[^1];
    }

    private static double Quantile(double[] sorted, double q)
    {
        if (sorted.Length == 0) return double.NaN;
        double pos = q * (sorted.Length - 1);
        int lo = (int)Math.Floor(pos);
        int hi = Math.Min(lo + 1, sorted.Length - 1);
        return sorted[lo] + (sorted[hi] - sorted[lo]) * (pos - lo);
    }

    private static JsonObject Verdict(string rule, string subject, string verdict, JsonObject evidence)
        => new()
        {
            ["rule"] = rule,
            ["subject"] = subject,
            ["verdict"] = verdict,
            ["evidence"] = evidence
        };
}
