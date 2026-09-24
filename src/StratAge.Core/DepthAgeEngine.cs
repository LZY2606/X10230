namespace StratAge.Core;

/// <summary>
/// Server-side, storage-independent rule engine. It enforces:
///  - ages are monotonic ONLY within continuous (non-faulted) segments;
///  - depth gaps and unconformities are explicit boundaries (no interpolation);
///  - an anchor distribution is never replaced by a mean (sample propagation);
///  - multiple equal-optimal models are all retained;
///  - budget exhaustion, corrupt format and semantic impossibility are distinct outcomes.
/// </summary>
public static class DepthAgeEngine
{
    public const string RuleMonotonic = "R-MONOTONIC";
    public const string RuleBoundary = "R-BOUNDARY";
    public const string RuleDistribution = "R-DISTRIBUTION";
    public const string RuleTied = "R-TIED";
    public const string RuleDuplicateDepth = "R-DUP-DEPTH";
    public const string RuleUnconvertible = "R-UNITS";
    public const string RuleBudget = "R-BUDGET";

    public static ModelResult Run(CaseFile cf, ModelingParameters p, long? budgetOverride = null)
    {
        var budget = budgetOverride ?? p.BudgetOps;
        var ops = 0L;
        bool Spend(long n)
        {
            ops += n;
            return ops <= budget;
        }

        var norm = Normalizer.Normalize(cf);
        var diags = new List<Diagnostic>(norm.Diagnostics);

        // Hard format/semantic preconditions from normalisation:
        var formatErrors = diags.Where(d => d.Code is "UNPARSEABLE_MEASURE" or "MISSING_UNIT"
            or "UNKNOWN_UNIT" or "DATUM_MISMATCH" or "TIME_BASIS_UNSUPPORTED").ToList();
        // Unconvertible values are *retained*, not fatal — they stay in place.
        var effective = new List<EffectiveAnchor>();
        foreach (var a in cf.Anchors)
        {
            var eff = AgeModelBuilder.TryBuild(a, norm, p, diags);
            if (eff is not null) effective.Add(eff);
        }

        var segById = cf.Segments.ToDictionary(s => s.Id);

        // 1) Duplicate depths at the same depth within ONE continuous segment
        //    are an unresolved semantic block unless they belong to a tie group.
        DetectDuplicateDepths(effective, diags);

        // 2) Explicit boundaries: nothing interpolates across them.
        ValidateBoundaries(cf, diags);

        // 3) Enumerate tie-group choices -> concrete anchor selections.
        var selections = EnumerateSelections(effective, diags);

        var candidates = new List<CandidateModel>();
        var interpModes = new[] { p.Interpolation, p.Interpolation == "linear" ? "pchip" : "linear" };
        var distinctModes = interpModes.Distinct().ToList();

        var anySemanticBlock = diags.Any(d =>
            d.Severity == Severity.Error &&
            (d.Code == "DUP_DEPTH" || d.Code == "MISSING_SEGMENT" || d.Code == "MISSING_RESERVOIR"));

        foreach (var sel in selections)
        {
            foreach (var mode in distinctModes)
            {
                if (!Spend(1))
                {
                    diags.Add(Normalizer.Diag(Severity.Error, "engine", "BUDGET_EXHAUSTED",
                        $"Resource budget of {budget} ops exhausted after {ops} ops; search stopped with partial, clearly-labelled results.",
                        RuleBudget, null));
                    return Finish(OutcomeClass.BudgetExhausted);
                }

                var (model, modelOps, tripped) = BuildCandidate(sel, mode, p, cf, segById, Spend);
                ops += modelOps;
                if (tripped)
                {
                    diags.Add(Normalizer.Diag(Severity.Error, "engine", "BUDGET_EXHAUSTED",
                        $"Resource budget of {budget} ops exhausted after {ops} ops; search stopped with partial, clearly-labelled results.",
                        RuleBudget, null));
                    return Finish(OutcomeClass.BudgetExhausted);
                }
                if (model is not null) candidates.Add(model);
            }
        }

        // Rank: fewer violations, then tighter integrated 90% interval (precision),
        // then deterministic id ordering so equivalent input gives a stable order.
        var ordered = candidates
            .OrderBy(c => c.Violations.Count(v => v.Severity == Severity.Error))
            .ThenBy(c => c.Score)
            .ThenBy(c => c.Id, StringComparer.Ordinal)
            .ToList();

        var bestScore = ordered.Count == 0 ? 0 :
            ordered.Where(c => c.Violations.Count(v => v.Severity == Severity.Error) ==
                ordered.Min(x => x.Violations.Count(v => v.Severity == Severity.Error)))
            .Min(c => c.Score);
        var minErr = ordered.Count == 0 ? 0 :
            ordered.Min(c => c.Violations.Count(v => v.Severity == Severity.Error));

        var ranked = ordered.Select((c, i) =>
        {
            var equal = c.Violations.Count(v => v.Severity == Severity.Error) == minErr &&
                        Math.Abs(c.Score - bestScore) < 1e-6;
            return c with { Rank = i + 1, EqualOptimal = equal };
        }).ToList();

        // Retain equal-optimal models; cap the rest to a deterministic budget.
        var kept = ranked.Take(p.MaxCandidates).ToList();

        var anchorEvals = EvaluateAnchors(cf, effective, p);
        var usableDepths = effective.Select(e => e.DepthM).OrderBy(x => x).ToList();

        var outcome = anySemanticBlock
            ? OutcomeClass.SemanticUnresolvable
            : (kept.All(k => k.Violations.Any(v => v.Severity == Severity.Error))
                ? OutcomeClass.SemanticUnresolvable : OutcomeClass.Ok);
        if (kept.Count == 0) outcome = OutcomeClass.SemanticUnresolvable;

        return new ModelResult
        {
            Outcome = outcome,
            ParameterVersion = p.Version,
            InputDigest = norm.Digest,
            Candidates = kept,
            Anchors = anchorEvals,
            Diagnostics = diags,
            OpsUsed = ops,
            BudgetOps = budget,
            EffectiveDepthRangeM = usableDepths.Count == 0 ? null
                : new[] { usableDepths[0], usableDepths[^1] }
        };

        ModelResult Finish(OutcomeClass o) => new()
        {
            Outcome = o, ParameterVersion = p.Version, InputDigest = norm.Digest,
            Candidates = candidates.OrderBy(c => c.Id, StringComparer.Ordinal)
                .Select((c, i) => c with { Rank = i + 1 }).Take(p.MaxCandidates).ToList(),
            Anchors = EvaluateAnchors(cf, effective, p),
            Diagnostics = diags, OpsUsed = ops, BudgetOps = budget,
            EffectiveDepthRangeM = null
        };
    }

    private static void DetectDuplicateDepths(List<EffectiveAnchor> anchors, List<Diagnostic> diags)
    {
        var bySegDepth = anchors
            .GroupBy(a => (a.SegmentId, Depth: Math.Round(a.DepthM, 6)))
            .Where(g => g.Count() > 1);

        foreach (var g in bySegDepth)
        {
            // Tied (mutually exclusive) alternatives at one depth are legal.
            var tieGroups = g.Select(a => a.TieGroup).Where(t => t is not null).Distinct().Count();
            var allTied = g.All(a => a.TieGroup is not null);
            if (allTied && tieGroups == g.Count()) continue;

            var ids = string.Join(", ", g.Select(a => a.Source.Id));
            diags.Add(Normalizer.Diag(Severity.Error,
                $"anchors@depth {g.Key.Depth}m/{g.Key.SegmentId}", "DUP_DEPTH",
                $"Duplicate control depths {ids} at {g.Key.Depth} m in segment {g.Key.SegmentId} are not declared as mutually-exclusive ties; a human decision is required before a unique model is legal.",
                RuleDuplicateDepth, ids));
        }
    }

    private static void ValidateBoundaries(CaseFile cf, List<Diagnostic> diags)
    {
        foreach (var b in cf.Boundaries)
        {
            if (b.Depth?.Usable != true) continue;
            var d = b.Depth.Canonical!.Value;
            var inside = cf.Segments.Any(s => s.Top?.Usable == true && s.Base?.Usable == true &&
                d > s.Top.Canonical!.Value && d < s.Base.Canonical!.Value);
            if (inside)
                diags.Add(Normalizer.Diag(Severity.Warning, $"boundaries/{b.Id}", "BOUNDARY_INSIDE_SEGMENT",
                    $"Boundary '{b.Name}' sits inside a single declared segment; consider splitting the segment. It is still treated as an explicit interpolation boundary.",
                    RuleBoundary, b.Id));
        }
    }

    /// <summary>Expands tied groups into all mutually-exclusive selections.</summary>
    private static List<List<EffectiveAnchor>> EnumerateSelections(
        List<EffectiveAnchor> anchors, List<Diagnostic> diags)
    {
        var fixedAnchors = anchors.Where(a => a.TieGroup is null).ToList();
        var groups = anchors.Where(a => a.TieGroup is not null)
            .GroupBy(a => a.TieGroup!)
            .OrderBy(g => g.Key, StringComparer.Ordinal)
            .ToList();

        var selections = new List<List<EffectiveAnchor>> { new(fixedAnchors) };
        foreach (var grp in groups)
        {
            var options = grp.OrderBy(a => a.Source.Id, StringComparer.Ordinal).ToList();
            diags.Add(Normalizer.Diag(Severity.Info, $"tieGroup/{grp.Key}", "TIED_ANCHORS",
                $"Tie group {grp.Key} has {options.Count} equally-defensible alternatives ({string.Join(", ", options.Select(o => o.Source.Id))}); every legal combination is retained.",
                RuleTied, grp.Key));
            var next = new List<List<EffectiveAnchor>>();
            foreach (var sel in selections)
                foreach (var opt in options)
                    next.Add(new List<EffectiveAnchor>(sel) { opt });
            selections = next;
        }
        return selections;
    }

    private static List<AnchorEvaluation> EvaluateAnchors(
        CaseFile cf, List<EffectiveAnchor> effective, ModelingParameters p)
    {
        var result = new List<AnchorEvaluation>();
        foreach (var a in cf.Anchors)
        {
            var eff = effective.FirstOrDefault(e => e.Source.Id == a.Id);
            if (a.Excluded)
            {
                result.Add(new AnchorEvaluation
                {
                    Id = a.Id, Usable = false, LongTail = false,
                    Reason = $"Excluded and retained: {a.ExcludeReason}",
                    Unit = a.Depth?.CanonicalUnit, TimeBasis = a.Age?.Datum
                });
                continue;
            }
            if (eff is null || a.Depth?.Usable != true)
            {
                result.Add(new AnchorEvaluation
                {
                    Id = a.Id, Usable = false, LongTail = false,
                    Reason = a.Depth?.Diagnostic ?? a.Age?.Diagnostic ?? "Not usable",
                    Unit = null, TimeBasis = a.Age?.Datum
                });
                continue;
            }
            var samples = AgeDistribution.ResampleSorted(eff.Distribution, p.SampleCount,
                AgeModelBuilder.StreamOf(a.Id));
            result.Add(new AnchorEvaluation
            {
                Id = a.Id, DepthM = eff.DepthM,
                MedianKa = AgeDistribution.QuantileSorted(samples, 0.5),
                P05Ka = AgeDistribution.QuantileSorted(samples, 0.05),
                P95Ka = AgeDistribution.QuantileSorted(samples, 0.95),
                LongTail = eff.LongTail, Usable = true,
                Unit = "m (" + cf.DepthDatum + ")", TimeBasis = a.Age?.Datum
            });
        }
        return result;
    }

    private static (CandidateModel? model, long ops, bool tripped) BuildCandidate(
        List<EffectiveAnchor> sel, string mode, ModelingParameters p, CaseFile cf,
        Dictionary<string, Segment> segById, Func<long, bool> spend)
    {
        var tieChoices = sel.Where(a => a.TieGroup is not null)
            .ToDictionary(a => a.TieGroup!, a => a.Source.Id);
        var choiceSig = string.Join("|", tieChoices.OrderBy(k => k.Key).Select(k => $"{k.Key}={k.Value}"));
        var id = $"cand-{mode}-{(choiceSig.Length == 0 ? "base" : choiceSig)}";

        var violations = new List<Diagnostic>();
        long ops = 0;

        // Monotonicity is tested PER continuous segment on posterior samples;
        // boundaries reset the check so gaps/unconformities can produce any jump.
        var perSegment = sel.GroupBy(a => a.SegmentId);
        var band = new List<UncertaintyBandPoint>();
        var rates = new List<IntervalRate>();
        double integratedWidth = 0;

        foreach (var grp in perSegment.OrderBy(g => g.Key, StringComparer.Ordinal))
        {
            if (!segById.TryGetValue(grp.Key, out var segment)) continue;
            var ordered = grp.OrderBy(a => a.DepthM).ToList();
            var overturned = segment.Overturned;

            for (var i = 1; i < ordered.Count; i++)
            {
                ops += p.SampleCount;
                if (!spend(p.SampleCount))
                    return (null, ops, true);

                var upper = ordered[i - 1];
                var lower = ordered[i];
                var su = upper.Distribution.Sample(p.SampleCount, AgeModelBuilder.StreamOf(upper.Source.Id));
                var sl = lower.Distribution.Sample(p.SampleCount, AgeModelBuilder.StreamOf(lower.Source.Id));

                var violations_ = 0;
                for (var k = 0; k < p.SampleCount; k++)
                {
                    // Normal: deeper (higher depth) must be older (larger ka BP).
                    // Overturned: stratigraphy inverted, so deeper gets younger.
                    var bad = overturned ? sl[k] > su[k] : sl[k] < su[k];
                    if (bad) violations_++;
                }
                var frac = (double)violations_ / p.SampleCount;
                if (frac >= p.HardInversionThreshold)
                    violations.Add(Normalizer.Diag(Severity.Error,
                        $"segments/{grp.Key}/anchors/{upper.Source.Id}->{lower.Source.Id}",
                        "AGE_INVERSION",
                        (overturned
                            ? $"Overturned segment {grp.Key}: deeper anchor {lower.Source.Id} is older than {upper.Source.Id} in {frac:P0} of samples, contradicting overturned order."
                            : $"Age decreases with depth between {upper.Source.Id} and {lower.Source.Id} in {frac:P0} of samples within continuous segment {grp.Key}."),
                        RuleMonotonic, $"{upper.Source.Id}|{lower.Source.Id}"));
                else if (frac >= p.UncertainOverlapThreshold)
                    violations.Add(Normalizer.Diag(Severity.Warning,
                        $"segments/{grp.Key}/anchors/{upper.Source.Id}->{lower.Source.Id}",
                        "AGE_OVERLAP_UNCERTAIN",
                        $"Anchor distributions overlap the monotonic bound in {frac:P1} of samples ({upper.Source.Id}/{lower.Source.Id}); ordering is supported but uncertain.",
                        RuleMonotonic, $"{upper.Source.Id}|{lower.Source.Id}"));
            }

            // Rates and uncertainty band inside this segment only.
            var curve = Interpolate.SegmentCurve(segment, ordered, mode, p, ref ops, spend);
            if (curve.Tripped) return (null, ops, true);
            rates.AddRange(curve.Rates);
            band.AddRange(curve.Band);
            integratedWidth += curve.Width;
        }

        var model = new CandidateModel
        {
            Id = id, Rank = 0, Score = integratedWidth, EqualOptimal = false,
            Interpolation = mode,
            ChosenAnchors = sel.OrderBy(a => a.Source.Id, StringComparer.Ordinal)
                .Select(a => a.Source.Id).ToList(),
            TieChoices = tieChoices,
            Rates = rates.OrderBy(r => r.Id, StringComparer.Ordinal).ToList(),
            Band = band.OrderBy(b => b.SegmentId).ThenBy(b => b.DepthM).ToList(),
            Violations = violations
        };
        return (model, ops, false);
    }
}
