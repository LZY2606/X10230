using System.Globalization;

namespace DepthAge.Domain;

/// <summary>Internal input bundle for one continuity group.</summary>
internal sealed record GroupInput
{
    public required string GroupKey { get; init; }
    public required string GroupDigest { get; init; }
    public required Segment[] Segments { get; init; }
    public required Anchor[] Anchors { get; init; }
    public required AnchorAge[] CalendarAges { get; init; }
}

internal sealed record AnchorAge(Anchor Anchor, AgeDist Calendar, double Depth);

/// <summary>
/// Server-side age-depth derivation. Builds continuity groups across explicit boundaries
/// (unconformities/gaps are boundaries, never spanned), enumerates every defensible
/// candidate over duplicate-depth options and interpolators, propagates full anchor
/// distributions by deterministic stratified sampling, and keeps all equally-best models.
/// </summary>
public sealed class DerivationEngine
{
    private const double DepthTol = 1e-9;

    public DerivationRecord Derive(BranchState state, RuleSet rules, DeriveOptions options,
        IReadOnlyList<DerivationRecord> priors, string actor, string timestamp)
    {
        var diagnostics = new List<Diagnostic>();
        var groups = BuildGroups(state, diagnostics);

        var results = new List<GroupResult>();
        var reused = new List<string>();
        var recomputed = new List<string>();

        foreach (var group in groups)
        {
            var priorMatch = priors
                .SelectMany(p => p.Groups)
                .FirstOrDefault(g => g.GroupDigest == group.GroupDigest);
            if (priorMatch is not null)
            {
                reused.Add(group.GroupDigest);
                results.Add(priorMatch);
                continue;
            }
            recomputed.Add(group.GroupDigest);
            results.Add(DeriveGroup(group, rules, options, diagnostics));
        }

        var status = AggregateStatus(results);
        var groupDigests = results.Select(g => g.GroupDigest).OrderBy(d => d).ToArray();
        var inputsDigest = Canon.StableId("inp", new
        {
            rules = rules.Version,
            groups = groupDigests
        });
        var idContent = new { rules = rules.Version, inputs = inputsDigest, groups = groupDigests };

        return new DerivationRecord
        {
            DerivationId = Canon.StableId("drv", idContent),
            RuleSetVersion = rules.Version,
            InputsDigest = inputsDigest,
            Status = status,
            Groups = results.OrderBy(g => g.GroupKey).ToArray(),
            Diagnostics = diagnostics.OrderBy(d => d.Position, StringComparer.Ordinal).ToArray(),
            ParentDependencies = groupDigests,
            ReusedGroupDigests = reused.OrderBy(d => d).ToArray(),
            RecomputedGroupDigests = recomputed.OrderBy(d => d).ToArray(),
            CreatedBy = actor,
            CreatedAt = timestamp
        };
    }

    private static DeriveStatus AggregateStatus(IReadOnlyList<GroupResult> results)
    {
        if (results.Count == 0) return DeriveStatus.Ok;
        if (results.Any(g => g.Status == DeriveStatus.SemanticNoSolution)) return DeriveStatus.SemanticNoSolution;
        if (results.Any(g => g.Status == DeriveStatus.BudgetExhausted)) return DeriveStatus.BudgetExhausted;
        return DeriveStatus.Ok;
    }

    // ---------------------------------------------------------------- continuity groups

    internal IReadOnlyList<GroupInput> BuildGroups(BranchState state, List<Diagnostic> diagnostics)
    {
        var segments = state.Segments
            .Where(s => s.Top.Value is not null && s.Bottom.Value is not null)
            .OrderBy(s => s.Top.Value!.Value)
            .ToList();

        foreach (var bad in state.Segments.Where(s => s.Top.Value is null || s.Bottom.Value is null))
            diagnostics.Add(Diag(DiagSeverity.Warning, "UnconvertibleDepth", $"segment:{bad.Id}",
                $"Segment '{bad.Label}' depth has no normalized value; excluded from derivation.", bad.Id));

        var groups = new List<List<Segment>>();
        var current = new List<Segment>();
        for (var i = 0; i < segments.Count; i++)
        {
            current.Add(segments[i]);
            if (i == segments.Count - 1)
            {
                groups.Add(current);
                break;
            }
            var above = segments[i];
            var below = segments[i + 1];
            var boundary = state.Boundaries.FirstOrDefault(b =>
                b.AboveSegmentId == above.Id && b.BelowSegmentId == below.Id);
            if (boundary is not null)
            {
                groups.Add(current);
                current = new List<Segment>();
                diagnostics.Add(Diag(DiagSeverity.Info, "ExplicitBoundary", $"boundary:{boundary.Id}",
                    $"{boundary.Kind} between {above.Label} and {below.Label}: monotonicity is not enforced across it.",
                    boundary.Id));
            }
            else if (Math.Abs(above.Bottom.Value!.Value - below.Top.Value!.Value) > DepthTol)
            {
                groups.Add(current);
                current = new List<Segment>();
                diagnostics.Add(Diag(DiagSeverity.Warning, "ImplicitGap", $"segment:{above.Id}|{below.Id}",
                    $"Segments {above.Label} and {below.Label} are not depth-adjacent and no explicit boundary " +
                    "was declared; treated as separate continuity groups.", null));
            }
        }

        var excluded = state.ActiveExcludedAnchorIds;
        var result = new List<GroupInput>();
        foreach (var groupSegments in groups)
        {
            var topDepth = groupSegments.Min(s => s.Top.Value!.Value);
            var bottomDepth = groupSegments.Max(s => s.Bottom.Value!.Value);
            var inGroup = state.Anchors
                .Where(a => a.Depth.Value is not null && !excluded.Contains(a.Id))
                .Where(a => a.Depth.Value!.Value >= topDepth - DepthTol &&
                            a.Depth.Value!.Value <= bottomDepth + DepthTol)
                .ToList();

            foreach (var outside in state.Anchors
                .Where(a => a.Depth.Value is not null && !excluded.Contains(a.Id))
                .Where(a => a.Depth.Value!.Value < topDepth - DepthTol ||
                            a.Depth.Value!.Value > bottomDepth + DepthTol))
                diagnostics.Add(Diag(DiagSeverity.Info, "AnchorOutsideSegments", $"anchor:{outside.Id}",
                    $"Anchor {outside.Id} is not inside any continuous segment; not used.", outside.Id));

            var ages = ResolveCalendarAges(inGroup, state, diagnostics);
            var segmentIds = groupSegments.Select(s => s.Id).OrderBy(s => s, StringComparer.Ordinal).ToArray();
            var groupKey = Canon.StableId("grp", segmentIds);
            var digest = GroupDigestOf(segmentIds, ages, rules: null, state);
            result.Add(new GroupInput
            {
                GroupKey = groupKey,
                GroupDigest = digest,
                Segments = groupSegments.ToArray(),
                Anchors = ages.Select(a => a.Anchor).ToArray(),
                CalendarAges = ages.ToArray()
            });
        }
        return result;
    }

    private static List<AnchorAge> ResolveCalendarAges(
        List<Anchor> anchors, BranchState state, List<Diagnostic> diagnostics)
    {
        var resolved = new List<AnchorAge>();
        foreach (var anchor in anchors)
        {
            AgeDist? calendar = null;
            if (anchor.Kind == AnchorKind.Radiocarbon)
            {
                if (anchor.ReservoirModelId is null ||
                    !state.ReservoirModels.TryGetValue(anchor.ReservoirModelId, out var model))
                {
                    diagnostics.Add(Diag(DiagSeverity.Error, "MissingReservoirModel", $"anchor:{anchor.Id}",
                        $"Radiocarbon anchor {anchor.Id} has no applicable reservoir model; cannot derive.",
                        anchor.Id));
                    continue;
                }
                // Simplified teaching calibration: reservoir offset is convolved (not mean-subtracted).
                var corrected = anchor.Age.Minus(model.Offset);
                calendar = corrected with { Unit = Units.CalAgeCanonical };
            }
            else if (anchor.CorrelationId is not null)
            {
                var correlation = state.Correlations.FirstOrDefault(c => c.Id == anchor.CorrelationId);
                if (correlation is null)
                {
                    diagnostics.Add(Diag(DiagSeverity.Error, "MissingCorrelation", $"anchor:{anchor.Id}",
                        $"Anchor {anchor.Id} references unknown event correlation {anchor.CorrelationId}.",
                        anchor.Id));
                    continue;
                }
                calendar = correlation.Age;
            }
            else
            {
                calendar = anchor.Age;
            }

            if (calendar.Unit != Units.CalAgeCanonical)
            {
                diagnostics.Add(Diag(DiagSeverity.Error, "UnitMismatch", $"anchor:{anchor.Id}",
                    $"Anchor {anchor.Id} age unit '{calendar.Unit}' is not '{Units.CalAgeCanonical}'.",
                    anchor.Id));
                continue;
            }

            if (calendar.IsLongTail())
                diagnostics.Add(Diag(DiagSeverity.Info, "LongTailDistribution", $"anchor:{anchor.Id}",
                    $"Anchor {anchor.Id} has a long-tailed age distribution; kept as a distribution, " +
                    "never replaced by its mean.", anchor.Id));

            resolved.Add(new AnchorAge(anchor, calendar, anchor.Depth.Value!.Value));
        }
        return resolved;
    }

    private static string GroupDigestOf(string[] segmentIds, List<AnchorAge> ages, RuleSet? rules, BranchState state)
    {
        var activeRs = state.ActiveRuleSetVersion;
        var anchorContent = ages
            .OrderBy(a => a.Depth)
            .ThenBy(a => a.Anchor.Id, StringComparer.Ordinal)
            .Select(a => new
            {
                id = a.Anchor.Id,
                depth = a.Depth.ToString("R", CultureInfo.InvariantCulture),
                dist = Canon.Serialize(a.Calendar),
                reservoir = a.Anchor.ReservoirModelId,
                correlation = a.Anchor.CorrelationId
            })
            .ToArray();
        var reservoirContent = state.ReservoirModels
            .Where(kv => ages.Any(a => a.Anchor.ReservoirModelId == kv.Key))
            .OrderBy(kv => kv.Key, StringComparer.Ordinal)
            .Select(kv => new { id = kv.Key, body = Canon.Serialize(kv.Value) })
            .ToArray();
        var correlationContent = state.Correlations
            .Where(c => ages.Any(a => a.Anchor.CorrelationId == c.Id))
            .OrderBy(c => c.Id, StringComparer.Ordinal)
            .Select(c => new { id = c.Id, body = Canon.Serialize(c) })
            .ToArray();
        return Canon.StableId("grpd", new
        {
            segments = segmentIds,
            rules = rules?.Version ?? activeRs,
            anchors = anchorContent,
            reservoirs = reservoirContent,
            correlations = correlationContent
        });
    }

    // --------------------------------------------------------------- candidate enumeration

    private GroupResult DeriveGroup(GroupInput group, RuleSet rules, DeriveOptions options,
        List<Diagnostic> diagnostics)
    {
        var groupDiags = new List<Diagnostic>();
        var sorted = group.CalendarAges
            .OrderBy(a => a.Depth)
            .ThenBy(a => a.Anchor.Id, StringComparer.Ordinal)
            .ToArray();

        if (sorted.Length < 2)
        {
            groupDiags.Add(Diag(DiagSeverity.Error, "SemanticNoSolution", $"group:{group.GroupKey}",
                "Fewer than two usable anchors in this continuous group; no age-depth model is possible.",
                group.GroupKey));
            return EmptyGroup(group, DeriveStatus.SemanticNoSolution, groupDiags);
        }

        var fixedAnchors = new List<AnchorAge>();
        var optionGroups = new List<List<List<AnchorAge>>>();
        foreach (var run in DepthRuns(sorted))
        {
            if (run.Count == 1)
            {
                fixedAnchors.Add(run[0]);
                continue;
            }
            var ids = string.Join(",", run.Select(a => a.Anchor.Id));
            if (rules.DuplicatePolicy == DuplicateDepthPolicy.Reject)
            {
                groupDiags.Add(Diag(DiagSeverity.Error, "DuplicateDepthRejected", $"anchor:{ids}",
                    $"Anchors {ids} share depth {run[0].Depth.ToString("R", CultureInfo.InvariantCulture)} m; " +
                    "rule set rejects duplicate-depth anchors.", ids));
                continue;
            }
            var choices = run.Select(a => new List<AnchorAge> { a }).ToList();
            if (run.Select(a => a.Calendar.Median()).DistinctBy(m => Math.Round(m, 9)).Count() == 1)
            {
                groupDiags.Add(Diag(DiagSeverity.Info, "TiedAnchors", $"anchor:{ids}",
                    $"Anchors {ids} are coeval at a shared depth; every choice is enumerated as an equal candidate.",
                    ids));
                choices.Add(run.ToList());
            }
            else
            {
                groupDiags.Add(Diag(DiagSeverity.Info, "DuplicateDepth", $"anchor:{ids}",
                    $"Anchors {ids} share a depth but differ in age; each choice is enumerated as a candidate.",
                    ids));
            }
            optionGroups.Add(choices);
        }

        var combos = CartesianChoices(fixedAnchors, optionGroups);
        if (combos.Count == 0)
        {
            groupDiags.Add(Diag(DiagSeverity.Error, "SemanticNoSolution", $"group:{group.GroupKey}",
                "No legal anchor combination remains after duplicate-depth handling.", group.GroupKey));
            return EmptyGroup(group, DeriveStatus.SemanticNoSolution, groupDiags);
        }

        var workload = combos.Count * rules.Interpolators.Length * Math.Max(1, rules.Realizations);
        if (combos.Count > rules.MaxCandidates ||
            (options.RealizationBudget is int budget && workload > budget))
        {
            groupDiags.Add(Diag(DiagSeverity.Error, "BudgetExhausted", $"group:{group.GroupKey}",
                $"Candidate workload {workload} exceeds budget (maxCandidates={rules.MaxCandidates}, " +
                $"realizationBudget={options.RealizationBudget?.ToString() ?? "unbounded"}). " +
                "Result is budget exhaustion, not a semantic failure.", group.GroupKey));
            return EmptyGroup(group, DeriveStatus.BudgetExhausted, groupDiags);
        }

        var candidates = new List<CandidateModel>();
        foreach (var combo in combos)
        {
            var ordered = combo.OrderBy(a => a.Depth).ThenBy(a => a.Anchor.Id, StringComparer.Ordinal).ToList();
            var inverted = false;
            for (var i = 1; i < ordered.Count; i++)
            {
                var upperMedian = ordered[i - 1].Calendar.Median();
                var lowerMedian = ordered[i].Calendar.Median();
                if (upperMedian > lowerMedian + 1e-9)
                {
                    var pair = $"{ordered[i - 1].Anchor.Id},{ordered[i].Anchor.Id}";
                    groupDiags.Add(Diag(DiagSeverity.Error, "InvertedAges", $"anchor:{pair}",
                        $"Ages decrease down-section ({upperMedian:G6} > {lowerMedian:G6} cal yr BP); " +
                        "the segment is internally inverted and this candidate is illegal.", pair));
                    foreach (var interp in rules.Interpolators)
                        candidates.Add(RejectedCandidate(group.GroupKey, ordered, interp,
                            $"InvertedAges:{pair}"));
                    inverted = true;
                    break;
                }
            }
            if (inverted) continue;
            foreach (var interp in rules.Interpolators)
                candidates.Add(Propagate(group.GroupKey, ordered, interp, rules, groupDiags));
        }

        var valid = candidates.Where(c => c.RejectedReason is null).ToList();
        var status = valid.Count == 0 ? DeriveStatus.SemanticNoSolution : DeriveStatus.Ok;

        var best = valid.Count == 0 ? double.PositiveInfinity : valid.Min(c => c.Score);
        var tolerance = 1e-9 * Math.Max(1.0, Math.Abs(best));
        var ranked = valid.OrderBy(c => c.Score).ThenBy(c => c.CandidateKey, StringComparer.Ordinal).ToList();
        foreach (var candidate in candidates)
        {
            if (candidate.RejectedReason is not null)
            {
                candidate.Accepted = false;
                continue;
            }
            candidate.Rank = ranked.IndexOf(candidate) + 1;
            candidate.Accepted = candidate.Score <= best + tolerance;
        }

        diagnostics.AddRange(groupDiags);
        return new GroupResult
        {
            GroupKey = group.GroupKey,
            GroupDigest = group.GroupDigest,
            SegmentIds = group.Segments.Select(s => s.Id).OrderBy(s => s, StringComparer.Ordinal).ToArray(),
            TopDepth = group.Segments.Min(s => s.Top.Value!.Value),
            BottomDepth = group.Segments.Max(s => s.Bottom.Value!.Value),
            Status = status,
            AnchorIds = sorted.Select(a => a.Anchor.Id).ToArray(),
            Candidates = candidates
                .OrderBy(c => c.RejectedReason is null ? 0 : 1)
                .ThenBy(c => c.Rank)
                .ThenBy(c => c.CandidateKey, StringComparer.Ordinal)
                .ToArray(),
            Diagnostics = groupDiags.ToArray()
        };
    }

    private static GroupResult EmptyGroup(GroupInput group, DeriveStatus status, List<Diagnostic> diags) =>
        new()
        {
            GroupKey = group.GroupKey,
            GroupDigest = group.GroupDigest,
            SegmentIds = group.Segments.Select(s => s.Id).OrderBy(s => s, StringComparer.Ordinal).ToArray(),
            TopDepth = group.Segments.Min(s => s.Top.Value!.Value),
            BottomDepth = group.Segments.Max(s => s.Bottom.Value!.Value),
            Status = status,
            AnchorIds = group.CalendarAges.Select(a => a.Anchor.Id).ToArray(),
            Candidates = [],
            Diagnostics = diags.ToArray()
        };

    private static List<List<AnchorAge>> DepthRuns(AnchorAge[] sorted)
    {
        var runs = new List<List<AnchorAge>>();
        foreach (var item in sorted)
        {
            if (runs.Count == 0 || Math.Abs(runs[^1][0].Depth - item.Depth) > DepthTol)
                runs.Add(new List<AnchorAge> { item });
            else
                runs[^1].Add(item);
        }
        return runs;
    }

    private static List<List<AnchorAge>> CartesianChoices(
        List<AnchorAge> fixedAnchors, List<List<List<AnchorAge>>> optionGroups)
    {
        var combos = new List<List<AnchorAge>> { fixedAnchors.ToList() };
        foreach (var choices in optionGroups)
        {
            var next = new List<List<AnchorAge>>();
            foreach (var combo in combos)
                foreach (var choice in choices)
                    next.Add([.. combo, .. choice]);
            combos = next;
        }
        return combos;
    }

    // --------------------------------------------------------------- propagation

    private static CandidateModel RejectedCandidate(string groupKey, List<AnchorAge> anchors,
        InterpolatorKind interp, string reason) =>
        new()
        {
            CandidateKey = CandidateKeyOf(groupKey, anchors, interp),
            GroupKey = groupKey,
            Interpolator = interp,
            AnchorIds = anchors.Select(a => a.Anchor.Id).ToArray(),
            Score = double.PositiveInfinity,
            RejectedReason = reason,
            MonotonicFraction = 0,
            Realizations = 0,
            Band = [],
            Rates = []
        };

    private static string CandidateKeyOf(string groupKey, IReadOnlyList<AnchorAge> anchors,
        InterpolatorKind interp) =>
        Canon.StableId("cand", new
        {
            group = groupKey,
            interp,
            anchors = anchors.Select(a => a.Anchor.Id).ToArray()
        });

    private static CandidateModel Propagate(string groupKey, List<AnchorAge> anchors,
        InterpolatorKind interp, RuleSet rules, List<Diagnostic> diagnostics)
    {
        var n = Math.Max(8, rules.Realizations);
        var depths = anchors.Select(a => a.Depth).ToArray();
        var dists = anchors.Select(a => a.Calendar).ToArray();

        var perms = dists.Select(d => Sampling.Permutation(n, d.Median())).ToArray();
        var draws = new double[dists.Length][];
        for (var i = 0; i < dists.Length; i++)
        {
            draws[i] = new double[n];
            for (var r = 0; r < n; r++)
                draws[i][r] = dists[i].StratifiedDraw(perms[i][r], n);
        }

        var kept = new List<int>();
        for (var r = 0; r < n; r++)
        {
            var monotonic = true;
            for (var i = 1; i < draws.Length; i++)
                if (draws[i][r] < draws[i - 1][r] - 1e-9)
                {
                    monotonic = false;
                    break;
                }
            if (monotonic) kept.Add(r);
        }
        var fraction = kept.Count / (double)n;

        var key = CandidateKeyOf(groupKey, anchors, interp);
        if (fraction < rules.MinMonotonicFraction)
        {
            diagnostics.Add(Diag(DiagSeverity.Warning, "MonotonicFractionLow", $"candidate:{key}",
                $"Only {fraction:P0} of realizations are downcore-monotonic " +
                $"(minimum {rules.MinMonotonicFraction:P0}); candidate rejected.", key));
            return new CandidateModel
            {
                CandidateKey = key,
                GroupKey = groupKey,
                Interpolator = interp,
                AnchorIds = anchors.Select(a => a.Anchor.Id).ToArray(),
                Score = double.PositiveInfinity,
                RejectedReason = $"MonotonicFraction {fraction:F2} < {rules.MinMonotonicFraction:F2}",
                MonotonicFraction = fraction,
                Realizations = n,
                Band = [],
                Rates = []
            };
        }

        var band = BuildBand(depths, draws, kept.ToArray(), interp, rules);
        var rates = BuildRates(depths, draws, kept.ToArray());
        var score = Roughness(band);

        return new CandidateModel
        {
            CandidateKey = key,
            GroupKey = groupKey,
            Interpolator = interp,
            AnchorIds = anchors.Select(a => a.Anchor.Id).ToArray(),
            Score = score,
            MonotonicFraction = fraction,
            Realizations = n,
            Band = band,
            Rates = rates
        };
    }

    private static BandPoint[] BuildBand(double[] depths, double[][] draws, int[] kept,
        InterpolatorKind interp, RuleSet rules)
    {
        var top = depths.Min();
        var bottom = depths.Max();
        var span = Math.Max(bottom - top, 1e-9);
        var pad = 0.15 * span;
        var gridTop = top - pad;
        var gridBottom = bottom + pad;
        const int gridCount = 41;
        var points = new List<BandPoint>();

        for (var g = 0; g < gridCount; g++)
        {
            var depth = gridTop + (gridBottom - gridTop) * g / (gridCount - 1);
            var constrained = depth >= top - DepthTol && depth <= bottom + DepthTol;
            var values = new double[kept.Length];
            for (var k = 0; k < kept.Length; k++)
            {
                var r = kept[k];
                var series = draws.Select(d => d[r]).ToArray();
                values[k] = constrained
                    ? Interpolate(interp, depths, series, depth)
                    : Extrapolate(depths, series, depth);
            }
            Array.Sort(values);
            points.Add(new BandPoint
            {
                Depth = depth,
                Median = QuantileOfSorted(values, 0.5),
                Low = QuantileOfSorted(values, rules.BandLow),
                High = QuantileOfSorted(values, rules.BandHigh),
                Constrained = constrained
            });
        }
        return points.ToArray();
    }

    private static SpanRate[] BuildRates(double[] depths, double[][] draws, int[] kept)
    {
        var rates = new List<SpanRate>();
        for (var i = 1; i < depths.Length; i++)
        {
            var depthSpanCm = (depths[i] - depths[i - 1]) * 100.0;
            if (depthSpanCm <= 0) continue;
            var values = new double[kept.Length];
            for (var k = 0; k < kept.Length; k++)
            {
                var r = kept[k];
                var ageSpanKyr = (draws[i][r] - draws[i - 1][r]) / 1000.0;
                values[k] = ageSpanKyr > 0 ? depthSpanCm / ageSpanKyr : double.PositiveInfinity;
            }
            var finite = values.Where(v => !double.IsPositiveInfinity(v)).OrderBy(v => v).ToArray();
            if (finite.Length == 0) continue;
            rates.Add(new SpanRate
            {
                FromDepth = depths[i - 1],
                ToDepth = depths[i],
                Median = QuantileOfSorted(finite, 0.5),
                Low = QuantileOfSorted(finite, 0.05),
                High = QuantileOfSorted(finite, 0.95),
                Unit = "cm/kyr"
            });
        }
        return rates.ToArray();
    }

    private static double Roughness(BandPoint[] band)
    {
        var constrained = band.Where(b => b.Constrained).Select(b => b.Median).ToArray();
        if (constrained.Length < 3) return 0;
        var sum = 0.0;
        for (var i = 1; i < constrained.Length - 1; i++)
            sum += Math.Abs(constrained[i + 1] - 2 * constrained[i] + constrained[i - 1]);
        return sum;
    }

    private static double QuantileOfSorted(double[] sorted, double p)
    {
        if (sorted.Length == 0) return double.NaN;
        var pos = p * (sorted.Length - 1);
        var lo = (int)Math.Floor(pos);
        var hi = Math.Min(lo + 1, sorted.Length - 1);
        return sorted[lo] + (sorted[hi] - sorted[lo]) * (pos - lo);
    }

    // --------------------------------------------------------------- interpolation

    public static double Interpolate(InterpolatorKind kind, double[] xs, double[] ys, double x)
    {
        if (x <= xs[0]) return ys[0];
        if (x >= xs[^1]) return ys[^1];
        var i = 0;
        while (i < xs.Length - 2 && xs[i + 1] < x) i++;
        if (kind == InterpolatorKind.PiecewiseLinear)
        {
            var t = (x - xs[i]) / (xs[i + 1] - xs[i]);
            return ys[i] + t * (ys[i + 1] - ys[i]);
        }
        var slopes = PchipSlopes(xs, ys);
        var h = xs[i + 1] - xs[i];
        var u = (x - xs[i]) / h;
        var u2 = u * u;
        var u3 = u2 * u;
        return (2 * u3 - 3 * u2 + 1) * ys[i]
             + (u3 - 2 * u2 + u) * h * slopes[i]
             + (-2 * u3 + 3 * u2) * ys[i + 1]
             + (u3 - u2) * h * slopes[i + 1];
    }

    /// <summary>Extrapolation uses the boundary span's linear rate; flagged unconstrained.</summary>
    private static double Extrapolate(double[] xs, double[] ys, double x)
    {
        if (x < xs[0])
        {
            var slope = (ys[1] - ys[0]) / (xs[1] - xs[0]);
            return ys[0] + slope * (x - xs[0]);
        }
        var n = xs.Length;
        var endSlope = (ys[n - 1] - ys[n - 2]) / (xs[n - 1] - xs[n - 2]);
        return ys[n - 1] + endSlope * (x - xs[n - 1]);
    }

    /// <summary>Fritsch-Carlson monotone cubic slopes.</summary>
    private static double[] PchipSlopes(double[] xs, double[] ys)
    {
        var n = xs.Length;
        var h = new double[n - 1];
        var d = new double[n - 1];
        for (var i = 0; i < n - 1; i++)
        {
            h[i] = xs[i + 1] - xs[i];
            d[i] = (ys[i + 1] - ys[i]) / h[i];
        }
        var m = new double[n];
        if (n == 2)
        {
            m[0] = m[1] = d[0];
            return m;
        }
        for (var k = 1; k < n - 1; k++)
        {
            if (d[k - 1] * d[k] <= 0)
                m[k] = 0;
            else
            {
                var w1 = 2 * h[k] + h[k - 1];
                var w2 = h[k] + 2 * h[k - 1];
                m[k] = (w1 + w2) / (w1 / d[k - 1] + w2 / d[k]);
            }
        }
        m[0] = PchipEndSlope(h[0], h[1], d[0], d[1]);
        m[n - 1] = PchipEndSlope(h[n - 2], h[n - 3], d[n - 2], d[n - 3]);
        return m;
    }

    private static double PchipEndSlope(double h0, double h1, double d0, double d1)
    {
        var slope = ((2 * h0 + h1) * d0 - h0 * d1) / (h0 + h1);
        if (Math.Sign(slope) != Math.Sign(d0)) return 0;
        if (Math.Sign(d0) != Math.Sign(d1) && Math.Abs(slope) > 3 * Math.Abs(d0))
            return 3 * d0;
        return slope;
    }

    internal static Diagnostic Diag(DiagSeverity severity, string code, string position,
        string message, string? objectId = null) =>
        new()
        {
            Code = code,
            Severity = severity,
            Position = position,
            Message = message,
            ObjectId = objectId
        };
}
