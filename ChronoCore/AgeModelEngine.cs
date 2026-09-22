using System.Diagnostics;

namespace ChronoCore;

/// <summary>
/// Server-side, directly testable derivation engine.
/// Rules (chrono-rules-1.0.0):
///  * monotonic age ordering is enforced ONLY within uninterrupted segments; gaps and
///    unconformities are explicit boundaries across which ordering is not required;
///  * every anchor carries a distribution; summaries are quantiles, never mean replacement;
///  * all legal tied candidates are enumerated and retained;
///  * enumeration is resource-bounded; exhaustion is a distinct outcome.
/// </summary>
public sealed class AgeModelEngine
{
    private readonly Stopwatch _clock = new();

    public BuildResult Build(
        MaterialsDoc materials,
        ParameterSet parameters,
        IReadOnlySet<string> excluded,
        int generation,
        string trigger,
        IReadOnlyCollection<string> changedAnchorIds = null,
        IReadOnlyDictionary<string, AgeModelCandidate> previousById = null)
    {
        _clock.Restart();
        var result = new BuildResult { Generation = generation, Trigger = trigger };
        var judgments = new List<Judgment>();

        var segments = OrderSegments(materials, result.Diagnostics);
        var anchors = materials.Anchors
            .Where(a => a.Depth.IsValid)
            .OrderBy(a => a.Depth.Metres.Value)
            .ThenBy(a => a.Id, StringComparer.Ordinal)
            .ToList();
        var usable = anchors.Where(a => !excluded.Contains(a.Id)).ToList();

        foreach (var a in materials.Anchors.Where(a => !a.Depth.IsValid || !a.RawAge.IsValid))
        {
            string msg = !a.Depth.IsValid ? a.Depth.Diagnostics : a.RawAge.Diagnostics;
            result.Diagnostics.Add(new Diagnostic
            {
                Code = msg.StartsWith("SEMANTIC") ? DiagnosticCodes.SemanticUnresolvable
                     : msg.StartsWith("UNCONVERTIBLE") ? DiagnosticCodes.Unconvertible
                     : DiagnosticCodes.IncompleteInput,
                Location = $"$.anchors[{a.Id}]",
                Message = msg
            });
            judgments.Add(new Judgment
            {
                Code = JudgmentCode.UnconvertibleValue, Status = "error",
                Subjects = { a.Id }, Detail = msg
            });
        }

        var depthGroups = usable.GroupBy(a => a.Depth.Metres.Value).Where(g => g.Count() > 1).ToList();
        foreach (var g in depthGroups)
        {
            var ids = g.Select(a => a.Id).OrderBy(x => x, StringComparer.Ordinal).ToList();
            judgments.Add(new Judgment
            {
                Code = JudgmentCode.DepthDuplicate, Status = "warning",
                Subjects = ids, Detail = $"{ids.Count} anchors share depth {g.Key:0.###} m",
                Evidence = { ["depthM"] = g.Key, ["anchorIds"] = ids }
            });
        }

        foreach (var a in usable.Where(a => a.Distribution.IsLongTail))
            judgments.Add(new Judgment
            {
                Code = JudgmentCode.DistributionLongTail, Status = "warning",
                Subjects = { a.Id },
                Detail = $"{a.Id} uses a {a.Distribution.Kind} distribution; median is retained"
            });

        // Assign anchors to the uninterrupted segment containing their depth.
        var bySegment = AssignAnchors(segments, usable, judgments);

        // Cross-segment pairs are NEVER contradictory: boundaries are explicit.
        var selectionBySegment = new Dictionary<string, List<List<Anchor>>>();
        foreach (var seg in segments)
        {
            var inSeg = bySegment.TryGetValue(seg.Id, out var v) ? v : new List<Anchor>();
            var selections = EnumerateSegmentSelections(seg, inSeg, parameters, judgments,
                out bool budgetHit);
            if (budgetHit)
            {
                result.Outcome = DiagnosticCodes.BudgetExhausted;
                judgments.Add(new Judgment
                {
                    Code = JudgmentCode.BudgetExhausted, Status = "error", SegmentId = seg.Id,
                    Detail = $"candidate budget {parameters.CandidateBudget} exhausted in {seg.Id}"
                });
            }
            selectionBySegment[seg.Id] = selections;
            if (_clock.ElapsedMilliseconds > parameters.CpuBudgetMs)
            {
                result.Outcome = DiagnosticCodes.BudgetExhausted;
                judgments.Add(new Judgment
                {
                    Code = JudgmentCode.BudgetExhausted, Status = "error", SegmentId = seg.Id,
                    Detail = $"CPU budget {parameters.CpuBudgetMs} ms exhausted"
                });
                break;
            }
        }

        if (result.Outcome == DiagnosticCodes.BudgetExhausted)
        {
            result.GlobalJudgments = judgments;
            return result;
        }

        var combos = Cartesian(segments, selectionBySegment);
        if (combos.Count > parameters.CandidateBudget)
        {
            result.Outcome = DiagnosticCodes.BudgetExhausted;
            judgments.Add(new Judgment
            {
                Code = JudgmentCode.BudgetExhausted, Status = "error",
                Detail = $"global combination count exceeds budget {parameters.CandidateBudget}"
            });
            result.GlobalJudgments = judgments;
            return result;
        }

        var candidates = combos.ConvertAll(combo => BuildCandidate(
            combo, segments, materials, parameters, judgments));

        // Stable ordering: score desc, then tie key. Equivalent input => identical order.
        candidates = candidates
            .OrderByDescending(c => c.Score)
            .ThenBy(c => c.TieKey, StringComparer.Ordinal)
            .ToList();

        double best = candidates.Count == 0 ? 0 : candidates[0].Score;
        var tied = candidates.Where(c => c.Score >= best - 1e-9).ToList();
        if (tied.Count > 1)
            judgments.Add(new Judgment
            {
                Code = JudgmentCode.TiedAnchor, Status = "tied",
                Subjects = tied.SelectMany(c => c.UsedAnchorIds).Distinct().OrderBy(x => x).ToList(),
                Detail = $"{tied.Count} optimal candidates are equally defensible; all retained"
            });
        foreach (var c in candidates)
            c.Judgments.AddRange(CloneGlobal(judgments));

        // Local recompute: preserve previous candidates whose used anchors are untouched.
        var changed = changedAnchorIds == null ? null : new HashSet<string>(changedAnchorIds);
        var previous = previousById ?? new Dictionary<string, AgeModelCandidate>();
        var finalCandidates = new List<AgeModelCandidate>();
        foreach (var c in candidates)
        {
            var rebuilt = c;
            var match = previous.TryGetValue(c.Id, out var old);
            if (match && changed != null && !c.UsedAnchorIds.Any(changed.Contains)
                && old.ParameterVersion == c.ParameterVersion)
            {
                result.PreservedCandidateIds.Add(c.Id);
                rebuilt = old;
                rebuilt.Status = old.Status == CandidateStatus.Frozen
                    ? CandidateStatus.Frozen : CandidateStatus.Proposed;
            }
            else
            {
                rebuilt.LineageGeneration = generation;
                rebuilt.LineageParentId = match ? old.LineageParentId : "";
                if (match && changed != null)
                {
                    rebuilt.LineageParentId = c.Id;
                    judgments.Add(new Judgment
                    {
                        Code = JudgmentCode.LocalRecompute, Status = "ok",
                        Subjects = c.UsedAnchorIds.Where(changed.Contains).ToList(),
                        Detail = $"recomputed {c.Id} locally from parent revision"
                    });
                }
            }
            finalCandidates.Add(rebuilt);
        }
        if (changed != null && changed.Count > 0)
            result.RecomputedAnchorIds = changed.OrderBy(x => x, StringComparer.Ordinal).ToList();

        result.Candidates = finalCandidates;
        result.GlobalJudgments = judgments;
        return result;
    }

    private static List<Judgment> CloneGlobal(List<Judgment> judgments)
        => judgments.Select(j => new Judgment
        {
            Code = j.Code, Status = j.Status, SegmentId = j.SegmentId, Detail = j.Detail,
            Subjects = j.Subjects.ToList(),
            Evidence = j.Evidence.ToDictionary(kv => kv.Key, kv => kv.Value)
        }).ToList();

    public static List<CoreSegment> OrderSegments(MaterialsDoc materials, List<Diagnostic> diagnostics)
    {
        var segments = materials.Segments
            .Where(s => s.Top.IsValid && s.Bottom.IsValid).ToList();
        foreach (var s in materials.Segments.Where(s => !s.Top.IsValid || !s.Bottom.IsValid))
            diagnostics.Add(new Diagnostic
            {
                Code = DiagnosticCodes.IncompleteInput, Location = $"$.segments[{s.Id}]",
                Message = "segment lacks valid top/bottom depth"
            });
        segments = segments.OrderBy(s => s.Top.Metres.Value).ToList();
        for (int i = 0; i < segments.Count; i++)
        {
            var s = segments[i];
            if (s.Bottom.Metres <= s.Top.Metres)
                diagnostics.Add(new Diagnostic
                {
                    Code = DiagnosticCodes.SemanticUnresolvable, Location = $"$.segments[{s.Id}]",
                    Message = $"inverted segment {s.Id}: bottom {s.Bottom.Raw} above top {s.Top.Raw}"
                });
            if (i > 0)
            {
                var prev = segments[i - 1];
                if (s.Top.Metres < prev.Bottom.Metres - 1e-6)
                    diagnostics.Add(new Diagnostic
                    {
                        Code = DiagnosticCodes.SemanticUnresolvable,
                        Location = $"$.segments[{prev.Id}|{s.Id}]",
                        Message = $"segments overlap: {prev.Id} and {s.Id}"
                    });
                double gap = s.Top.Metres.Value - prev.Bottom.Metres.Value;
                if (gap > 1e-6)
                {
                    if (prev.LowerBoundary == BoundaryKind.Continuous)
                        diagnostics.Add(new Diagnostic
                        {
                            Code = DiagnosticCodes.IncompleteInput,
                            Location = $"$.segments[{prev.Id}]",
                            Message = $"depth gap {gap:0.###} m between {prev.Id} and {s.Id} lacks explicit boundary"
                        });
                }
            }
        }
        return segments;
    }

    private static Dictionary<string, List<Anchor>> AssignAnchors(
        List<CoreSegment> segments, List<Anchor> anchors, List<Judgment> judgments)
    {
        var map = segments.ToDictionary(s => s.Id, _ => new List<Anchor>());
        foreach (var a in anchors)
        {
            var seg = segments.FirstOrDefault(s =>
                a.Depth.Metres >= s.Top.Metres - 1e-6 && a.Depth.Metres <= s.Bottom.Metres + 1e-6);
            if (seg != null) map[seg.Id].Add(a);
        }
        foreach (var seg in segments)
        {
            if (seg.LowerBoundary is BoundaryKind.Gap or BoundaryKind.Unconformity
                || seg.UpperBoundary is BoundaryKind.Gap or BoundaryKind.Unconformity)
                judgments.Add(new Judgment
                {
                    Code = JudgmentCode.UnconformityBoundary, Status = "ok", SegmentId = seg.Id,
                    Detail = $"{seg.Id} is bounded by an explicit {seg.LowerBoundary}/{seg.UpperBoundary}; order is not checked across it"
                });
        }
        return map;
    }

    private List<List<Anchor>> EnumerateSegmentSelections(
        CoreSegment seg, List<Anchor> anchors, ParameterSet p,
        List<Judgment> judgments, out bool budgetHit)
    {
        budgetHit = false;
        if (anchors.Count == 0) return new List<List<Anchor>> { new() };

        var groups = anchors
            .GroupBy(a => a.Depth.Metres.Value)
            .OrderBy(g => g.Key)
            .Select(g => g.OrderBy(a => a.Id, StringComparer.Ordinal).ToList())
            .ToList();

        // Contradictions only exist inside one uninterrupted segment.
        var conflicts = new HashSet<(string, string)>();
        var flat = anchors.OrderBy(a => a.Depth.Metres.Value).ToList();
        for (int i = 0; i < flat.Count; i++)
            for (int j = i + 1; j < flat.Count; j++)
            {
                var shallow = flat[i];
                var deep = flat[j];
                if (Math.Abs(shallow.Depth.Metres.Value - deep.Depth.Metres.Value) < 1e-9) continue;
                double pOlder = Sampling.ProbabilityOlder(deep, shallow, 500, p.RandomSeed);
                if (pOlder < p.OverlapTolerance)
                {
                    conflicts.Add((shallow.Id, deep.Id));
                    judgments.Add(new Judgment
                    {
                        Code = JudgmentCode.InvertedWithinSection, Status = "error",
                        SegmentId = seg.Id, Subjects = { shallow.Id, deep.Id },
                        Detail = $"inverted order in uninterrupted {seg.Id}: deeper {deep.Id} is older with only P={pOlder:0.###}",
                        Evidence = { ["pDeeperOlder"] = pOlder, ["boundary"] = BoundaryKind.Continuous }
                    });
                }
            }

        var selections = new List<List<Anchor>>();
        int budget = p.CandidateBudget;
        bool hit = false;

        void Dfs(int gi, List<Anchor> chosen)
        {
            if (selections.Count >= budget) { hit = true; return; }
            if (gi == groups.Count) { selections.Add(chosen.ToList()); return; }
            foreach (var option in groups[gi])
            {
                if (chosen.Any(c => conflicts.Contains((c.Id, option.Id))
                    || conflicts.Contains((option.Id, c.Id))))
                {
                    // Dropping this option in this selection is the local hitting-set choice.
                    continue;
                }
                chosen.Add(option);
                Dfs(gi + 1, chosen);
                chosen.RemoveAt(chosen.Count - 1);
            }
        }

        Dfs(0, new List<Anchor>());
        budgetHit = hit;
        if (selections.Count == 0)
            judgments.Add(new Judgment
            {
                Code = JudgmentCode.SemanticUnresolvable, Status = "error", SegmentId = seg.Id,
                Detail = $"no monotonic anchor selection exists within {seg.Id}"
            });
        return selections;
    }

    private static List<Dictionary<string, List<Anchor>>> Cartesian(
        List<CoreSegment> segments, Dictionary<string, List<List<Anchor>>> bySegment)
    {
        var combos = new List<Dictionary<string, List<Anchor>>> { new() };
        foreach (var seg in segments)
        {
            var next = new List<Dictionary<string, List<Anchor>>>();
            foreach (var combo in combos)
                foreach (var selection in bySegment[seg.Id])
                {
                    var copy = new Dictionary<string, List<Anchor>>(combo)
                    {
                        [seg.Id] = selection
                    };
                    next.Add(copy);
                }
            combos = next;
        }
        return combos;
    }

    private static AgeModelCandidate BuildCandidate(
        Dictionary<string, List<Anchor>> combo,
        List<CoreSegment> segments,
        MaterialsDoc materials,
        ParameterSet p,
        List<Judgment> judgments)
    {
        var used = combo.Values.SelectMany(x => x)
            .OrderBy(a => a.Depth.Metres.Value).ThenBy(a => a.Id, StringComparer.Ordinal).ToList();
        var usedIds = used.Select(a => a.Id).ToList();
        var allIds = materials.Anchors
            .Where(a => a.Depth.IsValid).Select(a => a.Id).ToHashSet();
        var skipped = allIds.Except(usedIds).OrderBy(x => x, StringComparer.Ordinal).ToList();

        var candidate = new AgeModelCandidate
        {
            ParameterVersion = p.Version,
            UsedAnchorIds = usedIds,
            SkippedAnchorIds = skipped
        };

        // Deterministic joint draws, one RNG stream per anchor.
        var streams = used.ToDictionary(a => a.Id,
            a => new DeterministicRng(Sampling.SeedFor(p.RandomSeed, a.Id)));
        int draws = p.MonteCarloDraws;
        var drawCache = used.ToDictionary(a => a.Id,
            a =>
            {
                var arr = new double[draws];
                for (int i = 0; i < draws; i++) arr[i] = Sampling.DrawEffectiveAge(a, streams[a.Id]);
                return arr;
            });

        double minDepth = segments.Count == 0 ? 0 : segments.Min(s => s.Top.Metres.Value);
        double maxDepth = segments.Count == 0 ? 0 : segments.Max(s => s.Bottom.Metres.Value);

        var depthGrid = new List<double>();
        for (double d = minDepth; d <= maxDepth + 1e-9; d += p.GridStepMetres)
            depthGrid.Add(Math.Round(d, 4));
        if (depthGrid.Count == 0 || Math.Abs(depthGrid[^1] - maxDepth) > 1e-9) depthGrid.Add(maxDepth);

        var gridBands = new List<(double depth, QuantileBand band)>();
        int gi = 0;
        foreach (var depth in depthGrid)
        {
            var seg = segments.FirstOrDefault(s =>
                depth >= s.Top.Metres - 1e-6 && depth <= s.Bottom.Metres + 1e-6);
            var segAnchors = seg == null ? new List<Anchor>()
                : combo.TryGetValue(seg.Id, out var sel) ? sel : new List<Anchor>();
            var ordered = segAnchors.OrderBy(a => a.Depth.Metres.Value).ToList();

            var samples = new double[draws];
            if (ordered.Count > 0)
            {
                var (lo, hi) = Bracket(ordered, depth);
                for (int k = 0; k < draws; k++)
                {
                    double age;
                    if (lo == null) age = drawCache[hi.Id][k];
                    else if (hi == null) age = drawCache[lo.Id][k];
                    else
                    {
                        double f = (depth - lo.Depth.Metres.Value)
                            / (hi.Depth.Metres.Value - lo.Depth.Metres.Value);
                        age = drawCache[lo.Id][k] * (1 - f) + drawCache[hi.Id][k] * f;
                        if (k > 0 && age < samples[k - 1]) age = samples[k - 1]; // isotonic guard
                    }
                    samples[k] = age;
                }
            }
            else
            {
                // No anchors in this uninterrupted segment: band is undefined, not faked.
                candidate.Grid.Add(new GridPoint
                {
                    Id = $"GP-{gi:D4}", DepthM = depth,
                    Age = new QuantileBand()
                });
                gi++;
                continue;
            }
            var band = Sampling.Summarize(samples);
            gridBands.Add((depth, band));
            candidate.Grid.Add(new GridPoint { Id = $"GP-{gi:D4}", DepthM = depth, Age = band });
            gi++;
        }

        foreach (var seg in segments)
        {
            var ordered = (combo.TryGetValue(seg.Id, out var sel) ? sel : new List<Anchor>())
                .OrderBy(a => a.Depth.Metres.Value).ToList();
            if (ordered.Count < 2) continue;
            var ypm = new List<double>();
            var cpy = new List<double>();
            for (int k = 0; k < draws; k++)
                for (int i = 0; i + 1 < ordered.Count; i++)
                {
                    double da = Math.Abs(drawCache[ordered[i + 1].Id][k] - drawCache[ordered[i].Id][k]);
                    double dd = ordered[i + 1].Depth.Metres.Value - ordered[i].Depth.Metres.Value;
                    if (dd <= 1e-9) continue;
                    double rate = da / dd;
                    ypm.Add(rate);
                    cpy.Add(rate > 0 ? 100.0 / rate : double.PositiveInfinity);
                }
            candidate.Rates.Add(new RateInterval
            {
                SegmentId = seg.Id,
                DepthFromM = ordered.First().Depth.Metres.Value,
                DepthToM = ordered.Last().Depth.Metres.Value,
                YearsPerMetre = Sampling.Summarize(ypm),
                CmPerYear = Sampling.Summarize(cpy.Where(x => !double.IsInfinity(x)).ToList())
            });
        }

        candidate.Score = Score(candidate, used);
        candidate.TieKey = string.Join("|", usedIds);
        candidate.Id = Sampling.StableHash(new[]
        {
            RuleSet.Version, p.Version.ToString(),
            string.Join(",", usedIds),
            string.Join(",", skipped),
            materials.DepthDatum
        });
        return candidate;
    }

    private static (Anchor lo, Anchor hi) Bracket(List<Anchor> ordered, double depth)
    {
        Anchor lo = null, hi = null;
        foreach (var a in ordered)
        {
            if (a.Depth.Metres <= depth + 1e-9) lo = a;
            if (a.Depth.Metres >= depth - 1e-9 && hi == null) hi = a;
        }
        if (lo != null && hi != null && lo.Id == hi.Id)
        {
            int idx = ordered.IndexOf(lo);
            if (depth < lo.Depth.Metres) hi = lo; else lo = idx > 0 ? ordered[idx - 1] : lo;
            if (depth > hi.Depth.Metres) lo = hi;
        }
        return (lo, hi);
    }

    private static double Score(AgeModelCandidate c, List<Anchor> used)
    {
        double pooled = used.Count == 0 ? 0 : used.Average(a => a.Distribution.StdYearsBp ?? 0);
        double spread = c.Rates.Count == 0 ? 0 :
            c.Rates.Average(r => r.YearsPerMetre.Std / Math.Max(r.YearsPerMetre.Mean, 1e-9));
        return used.Count * 1000.0 - pooled - spread * 100.0;
    }
}
