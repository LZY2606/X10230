using System.Text.Json;
using CoreChrono.Domain;

namespace CoreChrono.Engine;

public enum EngineStatus { Ok, Incomplete, BudgetExhausted, SemanticallyUnsolvable }

/// <summary>Result of deriving a depth-age model family.</summary>
public sealed class EngineResult
{
    public required EngineStatus Status { get; init; }
    public Derivation? Derivation { get; init; }
    public List<Judgment> Judgments { get; init; } = new();
    public List<ImportDiagnostic> Diagnostics { get; init; } = new();
}

/// <summary>One placement of anchors (which anchors/exclusion-sets participate).
/// Enumerating these is how the engine retains several defensible candidates.</summary>
internal sealed record AnchorChoice(HashSet<string> Used, List<string> Excluded, string ChoiceKey);

public sealed class DepthAgeEngine
{
    private readonly int _draws;
    private readonly ulong _seed;
    private readonly int _budget;

    public DepthAgeEngine(ParameterSet ps)
    {
        _draws = ps.MonteCarloDraws;
        _seed = ps.RandomSeed;
        _budget = ps.CandidateBudget;
    }

    // Public, directly testable entry point.
    public EngineResult Derive(EvidenceBundle ev, string derivationId, string parentEventId,
        string? anchorFocusId = null, string? frozenInputDigest = null)
    {
        var diag = new List<ImportDiagnostic>();
        var judg = new List<Judgment>();

        var activeSegments = ValidateSegments(ev, diag);
        var excludedNow = ev.Exclusions
            .Where(x => x.Status == DecisionStatus.Accepted)
            .Select(x => x.TargetId).ToHashSet();

        // Normalize anchors, applying reservoir correction on the derived layer only.
        var prepared = new List<PreparedAnchor>();
        foreach (var a in ev.Anchors)
        {
            if (excludedNow.Contains(a.Id)) continue;
            var p = PrepareAnchor(a, ev, diag);
            if (p is not null) prepared.Add(p);
        }

        // Tied anchors: anchors referencing the same event bed must share its datum age.
        ApplyTies(prepared, ev, judg);

        // Assign to segments and detect the structured boundary situations.
        var bySegment = AssignSegments(prepared, activeSegments, judg);

        if (diag.Any(d => d.Severity == "error"))
            return new EngineResult { Status = EngineStatus.Incomplete, Diagnostics = diag, Judgments = judg };

        ChoiceEnumeration choices;
        try { choices = EnumerateChoices(bySegment, judg, ev); }
        catch (BudgetOverflowException)
        {
            judg.Add(new Judgment { Code = "BUDGET_EXHAUSTED", Severity = "violation",
                Statement = $"minimal-fix enumeration exceeded budget {_budget}; refine inputs or raise the budget",
                AffectedIds = prepared.Select(x => x.Anchor.Id).ToList() });
            return new EngineResult { Status = EngineStatus.BudgetExhausted, Judgments = judg, Diagnostics = diag };
        }
        if (choices.Status == EngineStatus.BudgetExhausted)
            return new EngineResult { Status = EngineStatus.BudgetExhausted, Judgments = judg, Diagnostics = diag };
        if (choices.Status == EngineStatus.SemanticallyUnsolvable)
            return new EngineResult { Status = EngineStatus.SemanticallyUnsolvable, Judgments = judg, Diagnostics = diag };

        var methods = new[] { "linear", "pchip" };
        var candidates = new List<ModelCandidate>();
        var ci = 0;
        foreach (var choice in choices.Choices)
        {
            foreach (var method in methods)
            {
                ci++;
                var id = $"MC-{ci:D3}";
                var (cand, cjudg) = BuildCandidate(id, method, choice, bySegment, anchorFocusId);
                cand.Judgments.AddRange(cjudg);
                candidates.Add(cand);
            }
        }

        RankCandidates(candidates, judg);

        var inputDigest = ComputeInputDigest(ev);
        var derivation = new Derivation
        {
            Id = derivationId,
            ParamVersion = ev.Parameters.Version,
            InputDigest = inputDigest,
            ParentEventId = parentEventId,
            Status = "ok",
            Candidates = candidates,
            Judgments = judg,
            ActiveSegmentIds = activeSegments.Select(s => s.Id).ToList(),
            Frozen = frozenInputDigest is not null,
            FrozenInputDigest = frozenInputDigest,
        };

        return new EngineResult
        {
            Status = EngineStatus.Ok,
            Derivation = derivation,
            Judgments = judg,
            Diagnostics = diag,
        };
    }

    // ------------------------------------------------------------------
    // Validation & normalization
    // ------------------------------------------------------------------

    internal sealed record PreparedAnchor
    {
        public required AgeAnchor Anchor { get; init; }
        public required AgeDistribution Effective { get; init; } // after reservoir correction
        public double DepthM { get; init; }
        public double EffectiveMedianKa { get; init; }
        public bool DepthUnconvertible { get; init; }
    }

    private static List<Segment> ValidateSegments(EvidenceBundle ev, List<ImportDiagnostic> diag)
    {
        var result = new List<Segment>();
        if (ev.Datum is null)
            diag.Add(new ImportDiagnostic("DATUM_MISSING", "$.datum", "No depth datum declared", "error"));

        foreach (var s in ev.Segments)
        {
            var ok = true;
            if (!s.TopDepth.IsConvertible)
            {
                diag.Add(new ImportDiagnostic("UNCONVERTIBLE_DEPTH", $"segments[{s.Id}].top",
                    s.TopDepth.ConversionDiagnostic ?? "unconvertible", "error"));
                ok = false;
            }
            if (!s.BottomDepth.IsConvertible)
            {
                diag.Add(new ImportDiagnostic("UNCONVERTIBLE_DEPTH", $"segments[{s.Id}].bottom",
                    s.BottomDepth.ConversionDiagnostic ?? "unconvertible", "error"));
                ok = false;
            }
            if (ok && s.TopDepth.Normalized!.Value >= s.BottomDepth.Normalized!.Value)
                diag.Add(new ImportDiagnostic("SEGMENT_INVERTED", $"segments[{s.Id}]",
                    $"segment '{s.Label}' top ({s.TopDepth.Normalized}) is not above bottom ({s.BottomDepth.Normalized})",
                    "warning"));
            if (ok) result.Add(s);
        }

        // Gap/unconformity/fault boundaries are explicit; segments never span them silently.
        foreach (var s in result)
            if (s.LowerBoundary is BoundaryKind.Gap or BoundaryKind.Unconformity or BoundaryKind.Fault)
            {
                // informational structural judgment emitted later at assignment
            }
        return result;
    }

    private static PreparedAnchor? PrepareAnchor(AgeAnchor a, EvidenceBundle ev, List<ImportDiagnostic> diag)
    {
        if (!a.Depth.IsConvertible)
        {
            // Unconvertible value stays in place; do not drop it for plotting.
            diag.Add(new ImportDiagnostic("UNCONVERTIBLE_DEPTH", $"anchors[{a.Id}].depth",
                a.Depth.ConversionDiagnostic ?? "depth unconvertible", "error"));
            return new PreparedAnchor
            {
                Anchor = a,
                Effective = a.Distribution,
                DepthM = double.NaN,
                EffectiveMedianKa = double.NaN,
                DepthUnconvertible = true,
            };
        }

        var dist = a.Distribution;
        var off = ev.ReservoirOffsets.FirstOrDefault(o => o.Id == a.ReservoirOffsetId);
        if (off is not null)
        {
            if (!off.Offset.IsConvertible)
                diag.Add(new ImportDiagnostic("UNCONVERTIBLE_OFFSET", $"reservoirOffsets[{off.Id}]",
                    off.Offset.ConversionDiagnostic ?? "offset unconvertible", "error"));
            else
                dist = ShiftDistribution(dist, -off.Offset.Normalized!.Value);
        }
        return new PreparedAnchor
        {
            Anchor = a,
            Effective = dist,
            DepthM = a.Depth.Normalized!.Value,
            EffectiveMedianKa = dist.Quantile(0.5),
            DepthUnconvertible = false,
        };
    }

    /// <summary>Reservoir correction is a translation on the derived layer only;
    /// the measured distribution object is left untouched.</summary>
    private static AgeDistribution ShiftDistribution(AgeDistribution d, double deltaKa) => d switch
    {
        NormalAge n => n with { MeanKa = n.MeanKa + deltaKa },
        StudentTAge t => t with { LocationKa = t.LocationKa + deltaKa },
        LognormalAge l => new EmpiricalAge(SampleDistribution(l, 512, 991).Select(x => x + deltaKa).ToList()),
        EmpiricalAge e => new EmpiricalAge(e.SamplesKa.Select(x => x + deltaKa).ToList()),
        _ => d
    };

    private static void ApplyTies(List<PreparedAnchor> anchors, EvidenceBundle ev, List<Judgment> judg)
    {
        foreach (var grp in anchors.Where(x => x.Anchor.EventBedId is not null)
                                   .GroupBy(x => x.Anchor.EventBedId!))
        {
            var bed = ev.EventBeds.FirstOrDefault(b => b.Id == grp.Key);
            if (bed?.DatumAge is null)
            {
                judg.Add(new Judgment
                {
                    Code = "TIE_NO_DATUM_AGE",
                    Severity = "warning",
                    Statement = $"event bed '{bed?.Label ?? grp.Key}' has no datum age; tied anchors remain correlated but unresolved",
                    AffectedIds = grp.Select(x => x.Anchor.Id).ToList(),
                });
                continue;
            }
            // "并列锚点": multiple anchors tied to one event share a single datum distribution.
            var shared = bed.DatumAge;
            for (var i = 0; i < anchors.Count; i++)
                if (anchors[i].Anchor.EventBedId == grp.Key)
                    anchors[i] = anchors[i] with
                    {
                        Effective = shared,
                        EffectiveMedianKa = shared.Quantile(0.5)
                    };
            judg.Add(new Judgment
            {
                Code = "TIE_APPLIED",
                Severity = "info",
                Statement = $"{grp.Count()} anchor(s) tied to event bed '{bed.Label}' share datum distribution {shared.Describe()}",
                AffectedIds = grp.Select(x => x.Anchor.Id).Append(bed.Id).ToList(),
            });
        }
    }

    internal sealed record SegmentWorkspace(
        Segment Segment,
        List<PreparedAnchor> Anchors,
        List<Judgment> Judgments);

    private static Dictionary<string, SegmentWorkspace> AssignSegments(
        List<PreparedAnchor> anchors, List<Segment> segments, List<Judgment> judg)
    {
        var map = segments.ToDictionary(s => s.Id, s => new SegmentWorkspace(s, new(), new List<Judgment>()));

        foreach (var pa in anchors)
        {
            if (pa.DepthUnconvertible)
            {
                judg.Add(new Judgment
                {
                    Code = "ANCHOR_UNPLACED",
                    Severity = "diagnostic",
                    Statement = $"anchor '{pa.Anchor.Label}' depth cannot be converted; held in place, not plotted",
                    AffectedIds = new() { pa.Anchor.Id },
                });
                continue;
            }
            var home = pa.Anchor.SegmentId is not null && map.ContainsKey(pa.Anchor.SegmentId)
                ? map[pa.Anchor.SegmentId]
                : map.Values.FirstOrDefault(w =>
                    pa.DepthM >= w.Segment.TopDepth.Normalized!.Value - 1e-9 &&
                    pa.DepthM <= w.Segment.BottomDepth.Normalized!.Value + 1e-9);
            if (home is null)
            {
                judg.Add(new Judgment
                {
                    Code = "ANCHOR_OUTSIDE_SEGMENTS",
                    Severity = "warning",
                    Statement = $"anchor '{pa.Anchor.Label}' at {pa.DepthM} m falls in a gap/unconformity; it cannot interpolate across the boundary",
                    AffectedIds = new() { pa.Anchor.Id },
                });
                continue;
            }
            home.Anchors.Add(pa);
        }

        foreach (var w in map.Values)
        {
            AnalyzeWorkspace(w);
            judg.AddRange(w.Judgments);
        }

        // Gap/unconformity boundaries are explicit edges: flag segments that cannot join.
        foreach (var w in map.Values)
            if (w.Segment.LowerBoundary is BoundaryKind.Gap or BoundaryKind.Unconformity or BoundaryKind.Fault)
                judg.Add(new Judgment
                {
                    Code = "EXPLICIT_BOUNDARY",
                    Severity = "info",
                    Statement = $"'{w.Segment.Label}' ends in a {w.Segment.LowerBoundary.ToString().ToLowerInvariant()}; age monotonicity is not asserted across it",
                    AffectedIds = new() { w.Segment.Id },
                    SegmentId = w.Segment.Id,
                });

        return map;
    }

    private static void AnalyzeWorkspace(SegmentWorkspace w)
    {
        var ordered = w.Anchors.OrderBy(a => a.DepthM).ThenBy(a => a.Anchor.Id, StringComparer.Ordinal).ToList();

        // Duplicate depths within the same segment: ambiguous relative order.
        foreach (var g in ordered.GroupBy(a => a.DepthM).Where(g => g.Count() > 1))
        {
            var ids = g.Select(x => x.Anchor.Id).ToList();
            // If same event tie, it is expected; otherwise it is a true duplicate-depth conflict.
            var tieIds = g.Select(x => x.Anchor.EventBedId).Where(x => x is not null).Distinct().ToList();
            if (tieIds.Count == 1 && g.Count() == tieIds.Count + 0 && g.All(x => x.Anchor.EventBedId == tieIds[0]))
                continue;
            w.Judgments.Add(new Judgment
            {
                Code = "DEPTH_DUPLICATE",
                Severity = "violation",
                Statement = $"{g.Count()} anchors share depth {g.Key:G} m in '{w.Segment.Label}'; relative order cannot be resolved from depth alone",
                AffectedIds = ids,
                SegmentId = w.Segment.Id,
            });
        }

        // Monotonicity is asserted only within this (non-faulted) segment, in depth order.
        var usable = ordered.Where(a => !double.IsNaN(a.EffectiveMedianKa)).ToList();
        for (var i = 1; i < usable.Count; i++)
        {
            if (usable[i].EffectiveMedianKa < usable[i - 1].EffectiveMedianKa - 1e-9)
            {
                // Inverted section if the segment is explicitly overturned, else violation.
                var inverted = w.Segment.Ordering == -1;
                w.Judgments.Add(new Judgment
                {
                    Code = inverted ? "INVERTED_SECTION" : "AGE_INVERSION",
                    Severity = inverted ? "warning" : "violation",
                    Statement = inverted
                        ? $"segment '{w.Segment.Label}' is an inverted (overturned) section; ages decrease down-section by structure"
                        : $"age decreases down-section between '{usable[i - 1].Anchor.Label}' and '{usable[i].Anchor.Label}' inside a non-faulted segment",
                    AffectedIds = new() { usable[i - 1].Anchor.Id, usable[i].Anchor.Id },
                    SegmentId = w.Segment.Id,
                });
            }
        }
    }

    private sealed record ChoiceEnumeration(EngineStatus Status, List<AnchorChoice> Choices);

    /// <summary>Enumerate defensible anchor placements per segment, never silently
    /// spanning gaps/unconformities. Equivalent inputs yield a stable, ordered list.</summary>
    private ChoiceEnumeration EnumerateChoices(
        Dictionary<string, SegmentWorkspace> bySegment, List<Judgment> judg, EvidenceBundle ev)
    {
        var segmentOptions = new List<(string segId, List<HashSet<string>> keepSets)>();
        var allAnchorIds = bySegment.Values.SelectMany(w => w.Anchors.Select(a => a.Anchor.Id)).ToHashSet();

        foreach (var w in bySegment.Values.OrderBy(w => w.Segment.TopDepth.Normalized!.Value).ThenBy(w => w.Segment.Id, StringComparer.Ordinal))
        {
            var ordered = w.Anchors
                .OrderBy(a => a.DepthM)
                .ThenBy(a => a.Anchor.Id, StringComparer.Ordinal)
                .ToList();
            var keepOptions = new List<HashSet<string>> { new(ordered.Select(a => a.Anchor.Id)) };

            // 1) Duplicate-depth groups: choose exactly one member (equi-optimal),
            //    unless all share one event-bed tie (a deliberate parallel anchor set).
            foreach (var g in ordered.GroupBy(a => a.DepthM).Where(g => g.Count() > 1))
            {
                var members = g.Select(x => x.Anchor).ToList();
                var tieBeds = members.Select(m => m.EventBedId).Where(x => x is not null).Distinct().ToList();
                if (tieBeds.Count > 1)
                {
                    judg.Add(new Judgment
                    {
                        Code = "CONFLICTING_TIE",
                        Severity = "violation",
                        Statement = $"anchors at {g.Key:G} m in '{w.Segment.Label}' are tied to different event beds; no unique depth-age placement exists",
                        AffectedIds = members.Select(m => m.Id).ToList(),
                        SegmentId = w.Segment.Id,
                    });
                    return new ChoiceEnumeration(EngineStatus.SemanticallyUnsolvable, new());
                }
                if (tieBeds.Count == 1 && members.All(m => m.EventBedId == tieBeds[0]))
                    continue; // parallel tied anchors: intentionally co-located
                var pick = members;
                keepOptions = Cross(keepOptions, pick.Select(m => new HashSet<string> { m.Id }).ToList());
                judg.Add(new Judgment
                {
                    Code = "DUPLICATE_RESOLVED_BY_CHOICE",
                    Severity = "info",
                    Statement = $"depth {g.Key:G} m in '{w.Segment.Label}': {members.Count} equi-plausible anchors are retained as separate candidates",
                    AffectedIds = members.Select(m => m.Id).ToList(),
                    SegmentId = w.Segment.Id,
                });
            }

            // 2) Inversions in a normal (non-overturned) segment: keep only minimal
            //    exclusions that restore down-section monotonic ageing.
            var overturned = w.Segment.Ordering == -1;
            if (!overturned)
            {
                var fixOptions = MinimalMonotoneFixes(ordered);
                if (fixOptions is not null)
                {
                    keepOptions = Cross(keepOptions, fixOptions);
                    judg.Add(new Judgment
                    {
                        Code = "INVERSION_FIXES_ENUMERATED",
                        Severity = "info",
                        Statement = $"'{w.Segment.Label}': {fixOptions.Count} minimal defensible exclusion set(s) restore monotonic ageing; all retained",
                        AffectedIds = ordered.Select(a => a.Anchor.Id).ToList(),
                        SegmentId = w.Segment.Id,
                    });
                }
            }

            // dedupe keep sets deterministically
            keepOptions = keepOptions
                .Select(h => string.Join(",", h.OrderBy(x => x, StringComparer.Ordinal)))
                .Distinct()
                .OrderBy(x => x, StringComparer.Ordinal)
                .Select(s => s.Split(',', StringSplitOptions.RemoveEmptyEntries).ToHashSet())
                .ToList();
            segmentOptions.Add((w.Segment.Id, keepOptions));
        }

        // Combine across segments; gaps/unconformities mean choices multiply but
        // curves never interpolate across the boundaries.
        var combined = new List<HashSet<string>> { new() };
        foreach (var (_, opts) in segmentOptions)
            combined = Cross(combined, opts);

        if (combined.Count > _budget)
        {
            judg.Add(new Judgment
            {
                Code = "BUDGET_EXHAUSTED",
                Severity = "violation",
                Statement = $"candidate enumeration needs {combined.Count} placements but budget is {_budget}; refine inputs or raise the budget",
                AffectedIds = allAnchorIds.ToList(),
            });
            return new ChoiceEnumeration(EngineStatus.BudgetExhausted, new());
        }

        var choices = combined
            .OrderBy(h => string.Join(",", h.OrderBy(x => x, StringComparer.Ordinal)), StringComparer.Ordinal)
            .Select(h => new AnchorChoice(h,
                allAnchorIds.Where(id => !h.Contains(id)).OrderBy(x => x, StringComparer.Ordinal).ToList(),
                string.Join("|", h.OrderBy(x => x, StringComparer.Ordinal))))
            .ToList();
        if (choices.Count == 0) choices.Add(new AnchorChoice(new(), new(), ""));
        return new ChoiceEnumeration(EngineStatus.Ok, choices);
    }

    private static List<HashSet<string>> Cross(List<HashSet<string>> a, List<HashSet<string>> b)
    {
        var result = new List<HashSet<string>>(a.Count * b.Count);
        foreach (var x in a)
            foreach (var y in b)
            {
                var h = new HashSet<string>(x);
                h.UnionWith(y);
                result.Add(h);
            }
        return result;
    }

    /// <summary>Minimal exclusion sets restoring down-section non-decreasing age.
    /// They are exactly the complements of inclusion-maximal non-decreasing
    /// subsequences of effective medians. Enumeration is budget-bounded.</summary>
    private List<HashSet<string>>? MinimalMonotoneFixes(List<PreparedAnchor> ordered)
    {
        var n = ordered.Count;
        var age = ordered.Select(a => a.EffectiveMedianKa).ToArray();
        var inverted = false;
        for (var i = 1; i < n; i++) if (age[i] < age[i - 1] - 1e-9) inverted = true;
        if (!inverted) return null;

        var fixes = new List<HashSet<string>>();
        var seen = new HashSet<string>();
        var overflow = false;

        void Dfs(int start, double prevAge, List<int> chosen)
        {
            if (overflow) return;
            // Greedily extend over every feasible next index, branching to keep maximality.
            var feasible = new List<int>();
            for (var i = start; i < n; i++)
                if (age[i] >= prevAge - 1e-9) feasible.Add(i);
            if (feasible.Count == 0)
            {
                // chosen is a maximal non-decreasing subsequence from start=0 path only
                // when no skipped feasible element exists; maximality enforced by caller.
                Emit(chosen);
                return;
            }
            // To enumerate maximal subsequences, branch: pick each feasible element,
            // then ensure any skipped element cannot be re-inserted (it must violate
            // monotonicity relative to a later choice).
            foreach (var idx in feasible)
            {
                if (overflow) return;
                var next = new List<int>(chosen) { idx };
                // Verify maximality later; recurse.
                Dfs(idx + 1, age[idx], next);
            }
        }

        void Emit(List<int> keepIdx)
        {
            // maximality: no excluded index can be inserted preserving order
            var keepSet = keepIdx.ToHashSet();
            var sortedKeep = keepIdx.OrderBy(i => i).ToList();
            var canAdd = false;
            for (var i = 0; i < n; i++)
            {
                if (keepSet.Contains(i)) continue;
                var pos = sortedKeep.BinarySearch(i);
                if (pos < 0) pos = ~pos;
                var loOk = pos == 0 || age[sortedKeep[pos - 1]] <= age[i] + 1e-9;
                var hiOk = pos == sortedKeep.Count || age[i] <= age[sortedKeep[pos]] + 1e-9;
                if (loOk && hiOk) { canAdd = true; break; }
            }
            if (canAdd) return;
            var removed = Enumerable.Range(0, n).Where(i => !keepSet.Contains(i)).Select(i => ordered[i].Anchor.Id).ToHashSet();
            var key = string.Join(",", removed.OrderBy(x => x, StringComparer.Ordinal));
            if (seen.Add(key))
            {
                if (fixes.Count >= _budget) { overflow = true; return; }
                fixes.Add(removed);
            }
        }

        Dfs(0, double.NegativeInfinity, new List<int>());
        if (overflow)
            throw new BudgetOverflowException();
        return fixes.Count == 0 ? null : fixes;
    }

    public sealed class BudgetOverflowException : Exception { }

    // ------------------------------------------------------------------
    // Candidate construction: deterministic MC distributions + rate bands
    // ------------------------------------------------------------------

    private (ModelCandidate, List<Judgment>) BuildCandidate(
        string id, string method, AnchorChoice choice,
        Dictionary<string, SegmentWorkspace> bySegment, string? focusId)
    {
        var used = bySegment.Values
            .SelectMany(w => w.Anchors.Select(a => (w, a)))
            .Where(t => choice.Used.Contains(t.a.Anchor.Id))
            .ToList();

        var rates = new List<RateBand>();
        var band = new List<CurvePoint>();
        var localJudg = new List<Judgment>();

        foreach (var w in bySegment.Values.OrderBy(w => w.Segment.TopDepth.Normalized!.Value).ThenBy(w => w.Segment.Id, StringComparer.Ordinal))
        {
            var pts = used.Where(t => t.w.Segment.Id == w.Segment.Id)
                          .Select(t => t.a)
                          .OrderBy(a => a.DepthM)
                          .ThenBy(a => a.Anchor.Id, StringComparer.Ordinal)
                          .ToList();
            if (pts.Count == 0)
            {
                localJudg.Add(new Judgment
                {
                    Code = "SEGMENT_UNCONSTRAINED",
                    Severity = "warning",
                    Statement = $"segment '{w.Segment.Label}' has no usable anchor in this candidate",
                    AffectedIds = new() { w.Segment.Id },
                    SegmentId = w.Segment.Id,
                });
                continue;
            }
            BuildSegmentCurve(w, pts, method, rates, band);
        }

        if (focusId is not null && choice.Used.Contains(focusId))
            localJudg.Add(new Judgment
            {
                Code = "LOCAL_RECOMPUTE",
                Severity = "info",
                Statement = $"locally recomputed around anchor '{focusId}'; other segments reused from parent derivation",
                AffectedIds = new() { focusId },
            });

        var score = ScoreCandidate(band, choice);
        var cand = new ModelCandidate
        {
            Id = id,
            Rank = 0,
            Method = method,
            UsedAnchorIds = used.Select(t => t.a.Anchor.Id).OrderBy(x => x, StringComparer.Ordinal).ToList(),
            ExcludedAnchorIds = choice.Excluded,
            Score = score,
            EquiOptimal = false,
            TieGroup = 0,
            Rates = rates,
            Band = band,
        };
        return (cand, localJudg);
    }

    private void BuildSegmentCurve(SegmentWorkspace w, List<PreparedAnchor> pts, string method,
        List<RateBand> rates, List<CurvePoint> band)
    {
        var n = _draws;
        var depths = pts.Select(p => p.DepthM).ToArray();
        var streams = pts.Select(p =>
        {
            var key = p.Anchor.EventBedId ?? p.Anchor.Id;
            return new DeterministicRng(_seed ^ (ulong)key.GetHashCode(StringComparison.Ordinal) & 0x7FFFFFFFFFFFFFFFUL);
        }).ToArray();
        var ageDraws = new double[n][];
        for (var k = 0; k < n; k++)
            ageDraws[k] = pts.Select(p => 0.0).ToArray();
        for (var j = 0; j < pts.Count; j++)
        {
            var rng = streams[j];
            for (var k = 0; k < n; k++)
                ageDraws[k][j] = SampleDistribution(pts[j].Effective, rng.NextDouble());
        }

        // Build a depth grid strictly within this segment; never crossing its
        // explicit lower boundary (gap/unconformity/fault).
        var top = w.Segment.TopDepth.Normalized!.Value;
        var bot = w.Segment.BottomDepth.Normalized!.Value;
        var gridCount = Math.Max(8, 24);
        // include anchor depths in the grid
        var grid = new SortedSet<double>(depths);
        for (var i = 0; i <= gridCount; i++)
        {
            var d = top + (bot - top) * i / gridCount;
            if (d >= top - 1e-9 && d <= bot + 1e-9) grid.Add(Math.Round(d, 6));
        }
        var gridArr = grid.ToArray();

        foreach (var d in gridArr)
        {
            var column = new double[n];
            for (var k = 0; k < n; k++)
                column[k] = Interpolate(depths, ageDraws[k], d, method);
            Array.Sort(column);
            band.Add(new CurvePoint
            {
                DepthM = d,
                MedianKa = column[n / 2],
                Q05Ka = column[(int)(0.05 * (n - 1))],
                Q95Ka = column[(int)(0.95 * (n - 1))],
            });
        }

        // Sedimentation-rate interval between consecutive anchors (ka/m), per draw.
        for (var j = 1; j < depths.Length; j++)
        {
            var rate = new double[n];
            var dd = depths[j] - depths[j - 1];
            for (var k = 0; k < n; k++)
            {
                var ad = ageDraws[k][j] - ageDraws[k][j - 1];
                rate[k] = dd > 1e-12 ? ad / dd : double.NaN;
            }
            var valid = rate.Where(double.IsFinite).OrderBy(x => x).ToArray();
            if (valid.Length == 0) continue;
            rates.Add(new RateBand
            {
                SegmentId = w.Segment.Id,
                FromDepthM = depths[j - 1],
                ToDepthM = depths[j],
                MedianKaPerM = valid[valid.Length / 2],
                LowKaPerM = valid[(int)(0.05 * (valid.Length - 1))],
                HighKaPerM = valid[(int)(0.95 * (valid.Length - 1))],
                AnchorIds = new() { pts[j - 1].Anchor.Id, pts[j].Anchor.Id },
            });
        }
    }

    private static double Interpolate(double[] xs, double[] ys, double x, string method)
    {
        if (x <= xs[0]) return ys[0];
        if (x >= xs[^1]) return ys[^1];
        var i = 1;
        while (i < xs.Length && xs[i] < x) i++;
        var x0 = xs[i - 1]; var x1 = xs[i];
        var t = (x - x0) / (x1 - x0);
        if (method == "linear" || xs.Length < 3)
            return ys[i - 1] + t * (ys[i] - ys[i - 1]);
        // monotone PCHIP slopes
        return PchipAt(xs, ys, i, t);
    }

    private static double PchipAt(double[] xs, double[] ys, int i, double t)
    {
        double Slope(int a, int b) => (ys[b] - ys[a]) / (xs[b] - xs[a]);
        var h1 = xs[i] - xs[i - 1];
        var s1 = Slope(i - 1, i);
        double m0;
        if (i < 2) m0 = s1;
        else
        {
            var s0 = Slope(i - 2, i - 1);
            var h0 = xs[i - 1] - xs[i - 2];
            m0 = (s0 == 0 || s1 == 0 || Math.Sign(s0) != Math.Sign(s1)) ? 0
                : 3 * (h0 + h1) / ((2 * h1 + h0) / s0 + (h1 + 2 * h0) / s1);
        }
        double m1;
        if (i + 1 >= xs.Length) m1 = s1;
        else
        {
            var s2 = Slope(i, i + 1);
            var h2 = xs[i + 1] - xs[i];
            m1 = (s1 == 0 || s2 == 0 || Math.Sign(s1) != Math.Sign(s2)) ? 0
                : 3 * (h1 + h2) / ((2 * h2 + h1) / s1 + (h2 + 2 * h1) / s2);
        }
        var x0 = xs[i - 1]; var x1 = xs[i];
        var y0 = ys[i - 1]; var y1 = ys[i]; var h = x1 - x0;
        var h00 = 2 * t * t * t - 3 * t * t + 1;
        var h10 = t * t * t - 2 * t * t + t;
        var h01 = -2 * t * t * t + 3 * t * t;
        var h11 = t * t * t - t * t;
        return h00 * y0 + h10 * h * m0 + h01 * y1 + h11 * h * m1;
    }

    // ------------------------------------------------------------------
    // Sampling, scoring, ranking
    // ------------------------------------------------------------------

    internal static double[] SampleDistribution(AgeDistribution d, int n, ulong streamSeed)
    {
        var rng = new DeterministicRng(streamSeed);
        var outp = new double[n];
        for (var k = 0; k < n; k++) outp[k] = SampleDistribution(d, rng.NextDouble());
        return outp;
    }

    internal static double SampleDistribution(AgeDistribution d, double u)
    {
        u = Math.Clamp(u, 1e-9, 1 - 1e-9);
        return d switch
        {
            NormalAge nd => nd.MeanKa + nd.SigmaKa * MathNetLite.NormalFromU(u),
            StudentTAge td => td.LocationKa + td.ScaleKa * MathNetLite.TInv(u, td.Df),
            LognormalAge ld => ld.MedianKa * Math.Exp(ld.SigmaLog * MathNetLite.NormalFromU(u)),
            EmpiricalAge ed => ed.Quantile(u),
            _ => d.Quantile(u)
        };
    }

    /// <summary>Score: mean 90%-band width penalised by excluded anchors. Deterministic.</summary>
    private static double ScoreCandidate(List<CurvePoint> band, AnchorChoice choice)
    {
        if (band.Count == 0) return double.PositiveInfinity;
        var width = band.Average(p => Math.Max(0, p.Q95Ka - p.Q05Ka));
        return width + choice.Excluded.Count * 1000.0;
    }

    private static void RankCandidates(List<ModelCandidate> candidates, List<Judgment> judg)
    {
        // Stable total order: score, then method, then deterministic anchor set.
        var ordered = candidates
            .OrderBy(c => c.Score)
            .ThenBy(c => c.Method, StringComparer.Ordinal)
            .ThenBy(c => string.Join(",", c.UsedAnchorIds), StringComparer.Ordinal)
            .ThenBy(c => c.Id, StringComparer.Ordinal)
            .ToList();

        // Equal-optimal groups within a small numerical tolerance; ALL are retained.
        var tie = 0;
        double? groupScore = null;
        for (var i = 0; i < ordered.Count; i++)
        {
            ordered[i] = ordered[i] with { Rank = i + 1 };
            if (groupScore is null || Math.Abs(ordered[i].Score - groupScore.Value) > 1e-9)
            {
                tie++;
                groupScore = ordered[i].Score;
            }
            ordered[i] = ordered[i] with { TieGroup = tie };
        }
        var bestGroup = ordered[0].TieGroup;
        var best = ordered.Where(c => c.TieGroup == bestGroup).ToList();
        foreach (var c in best)
        {
            var idx = ordered.IndexOf(c);
            ordered[idx] = c with
            {
                EquiOptimal = true,
                SelectedReason = best.Count > 1
                    ? $"tied for best with {best.Count - 1} other candidate(s); all legal candidates retained"
                    : "single best candidate"
            };
        }
        if (best.Count > 1)
            judg.Add(new Judgment
            {
                Code = "MULTIPLE_EQUIOPTIMAL",
                Severity = "info",
                Statement = $"{best.Count} models are equally optimal; none is silently chosen",
                AffectedIds = best.Select(c => c.Id).ToList(),
            });
        candidates.Clear();
        candidates.AddRange(ordered);
    }

    private static string ComputeInputDigest(EvidenceBundle ev)
    {
        var core = string.Join("|", new[]
        {
            ev.Datum?.Id ?? "?",
            string.Join(";", ev.Segments.Select(s => $"{s.Id}:{s.TopDepth.Raw}:{s.BottomDepth.Raw}:{s.LowerBoundary}")),
            string.Join(";", ev.Anchors.Select(a => $"{a.Id}:{a.Depth.Raw}@{a.Depth.Unit}:{a.Distribution.Describe()}")),
            string.Join(";", ev.ReservoirOffsets.Select(o => $"{o.Id}:{o.Offset.Raw}")),
            string.Join(";", ev.EventBeds.Select(b => $"{b.Id}")),
            string.Join(";", ev.Exclusions.Where(x => x.Status == DecisionStatus.Accepted).Select(x => x.TargetId)),
            ev.Parameters.Version,
        });
        return Hashing.Sha256(core);
    }
}
