namespace ChronologyReview;

public sealed class ChronologyEngine
{
    public const string CurrentRuleVersion = "rules-2026.09-core-chronology-v1";

    public ModelResult Evaluate(
        ProjectSnapshot snapshot,
        string parameterVersion,
        int candidateBudget = 32,
        string? onlySegmentId = null)
    {
        var diagnostics = DiscoverBoundaryDiagnostics(snapshot, onlySegmentId);
        var excluded = ExcludedTargets(snapshot);
        var candidates = new List<ModelCandidate>();
        var affected = new SortedSet<string>(StringComparer.Ordinal);

        var segments = snapshot.Segments
            .Where(segment => onlySegmentId is null || segment.SegmentId == onlySegmentId)
            .OrderBy(segment => Math.Min(segment.TopDepth, segment.BottomDepth))
            .ThenBy(segment => segment.SegmentId, StringComparer.Ordinal)
            .ToList();

        foreach (var segment in segments)
        {
            affected.Add(segment.SegmentId);
            if (segment.TopDepth >= segment.BottomDepth)
            {
                diagnostics.Add(new Diagnostic(
                    "inverted-layer-segment",
                    "error",
                    "层段顶深必须小于底深；该段不参与单调年龄推导。",
                    segment.SegmentId,
                    segment.TopDepth,
                    segment.Unit,
                    [segment.SegmentId]));
                continue;
            }

            var anchors = snapshot.Anchors
                .Where(anchor => anchor.SegmentId == segment.SegmentId && !excluded.Contains(anchor.AnchorId))
                .OrderBy(anchor => anchor.Depth)
                .ThenBy(anchor => anchor.AnchorId, StringComparer.Ordinal)
                .ToList();

            if (anchors.Count == 0)
            {
                continue;
            }

            var groups = anchors.GroupBy(anchor => anchor.Depth)
                .OrderBy(group => group.Key)
                .Select(group => group.OrderBy(anchor => anchor.AnchorId, StringComparer.Ordinal).ToList())
                .ToList();

            var combinations = new List<List<AgeAnchor>> { new() };
            foreach (var group in groups)
            {
                if (group.Count > 1)
                {
                    diagnostics.Add(new Diagnostic(
                        "tied-anchors",
                        "warning",
                        "同一深度存在并列锚点；每个可辩护选择保留为独立候选。",
                        string.Join(",", group.Select(anchor => anchor.AnchorId)),
                        group.Key,
                        group[0].DepthUnit,
                        [segment.SegmentId]));
                }

                var next = new List<List<AgeAnchor>>();
                foreach (var combination in combinations)
                {
                    foreach (var anchor in group)
                    {
                        if (next.Count >= candidateBudget)
                        {
                            return new ModelResult(
                                RunStatus.BudgetExhausted,
                                [],
                                diagnostics,
                                affected.ToList(),
                                $"候选组合超过预算 {candidateBudget}；未用截断结果冒充完整推导。");
                        }
                        next.Add([.. combination, anchor]);
                    }
                }
                combinations = next;
            }

            foreach (var combination in combinations)
            {
                var expanded = ExpandInversionChoices(combination, segment, diagnostics, candidateBudget - candidates.Count);
                if (expanded is null)
                {
                    return new ModelResult(
                        RunStatus.BudgetExhausted,
                        [],
                        diagnostics,
                        affected.ToList(),
                        $"倒置层段修正候选超过预算 {candidateBudget}。");
                }

                foreach (var choice in expanded)
                {
                    if (choice.Count < 2)
                    {
                        continue;
                    }
                    candidates.Add(CreateCandidate(segment, choice, parameterVersion));
                }
            }
        }

        var distinct = candidates
            .GroupBy(candidate => candidate.CandidateId)
            .Select(group => group.First())
            .OrderBy(candidate => candidate.BlockId, StringComparer.Ordinal)
            .ThenBy(candidate => candidate.FitScore)
            .ThenBy(candidate => candidate.CandidateId, StringComparer.Ordinal)
            .ToList();

        if (distinct.Count == 0)
        {
            return new ModelResult(
                RunStatus.SemanticUnsolved,
                [],
                diagnostics,
                affected.ToList(),
                "没有足够且未被排除的锚点形成不断层段内的单调年龄路径。");
        }

        return new ModelResult(RunStatus.Success, distinct, diagnostics, affected.ToList(), "");
    }

    public List<Diagnostic> DiscoverBoundaryDiagnostics(ProjectSnapshot snapshot, string? onlySegmentId = null)
    {
        var diagnostics = new List<Diagnostic>();
        var segments = snapshot.Segments
            .Where(segment => onlySegmentId is null || segment.SegmentId == onlySegmentId)
            .OrderBy(segment => Math.Min(segment.TopDepth, segment.BottomDepth))
            .ToList();

        foreach (var boundary in snapshot.Boundaries.OrderBy(boundary => boundary.FromDepth).ThenBy(boundary => boundary.BoundaryId, StringComparer.Ordinal))
        {
            var code = boundary.Kind switch
            {
                "gap" => "depth-gap",
                "unconformity" => "unconformity",
                "duplicate-depth" => "depth-duplicate",
                "inversion" => "inverted-layer-segment",
                _ => "explicit-boundary"
            };
            diagnostics.Add(new Diagnostic(
                code,
                code is "depth-gap" or "unconformity" or "inverted-layer-segment" ? "error" : "warning",
                boundary.Reason,
                boundary.BoundaryId,
                boundary.FromDepth,
                boundary.Unit,
                segments.Select(segment => segment.SegmentId).ToList()));
        }

        foreach (var group in snapshot.Anchors.GroupBy(anchor => anchor.Depth).Where(group => group.Count() > 1))
        {
            diagnostics.Add(new Diagnostic(
                "depth-duplicate",
                "warning",
                "多个锚点使用相同深度；保留原始并列关系并由排除决定控制参与。",
                string.Join(",", group.Select(anchor => anchor.AnchorId).Order(StringComparer.Ordinal)),
                group.Key,
                group.First().DepthUnit,
                group.Select(anchor => anchor.SegmentId).Distinct(StringComparer.Ordinal).ToList()));
        }

        foreach (var anchor in snapshot.Anchors.OrderBy(anchor => anchor.Depth).ThenBy(anchor => anchor.AnchorId, StringComparer.Ordinal))
        {
            if (anchor.AgeDistributionKaBp.Count == 0)
            {
                diagnostics.Add(new Diagnostic(
                    "missing-age-distribution",
                    "error",
                    "锚点缺少年龄分布；不得以空值或均值伪装。",
                    anchor.AnchorId,
                    anchor.Depth,
                    anchor.DepthUnit,
                    [anchor.SegmentId]));
                continue;
            }

            var sorted = anchor.AgeDistributionKaBp.Order().ToArray();
            var median = Quantile(sorted, 0.50);
            var p95 = Quantile(sorted, 0.95);
            var max = sorted[^1];
            if (median > 0 && (p95 / median > 1.35 || max / Math.Max(p95, 0.000001) > 1.20))
            {
                diagnostics.Add(new Diagnostic(
                    "age-distribution-long-tail",
                    "warning",
                    "放射性年龄分布存在长尾；模型保留完整分布，不用均值替代。",
                    anchor.AnchorId,
                    anchor.Depth,
                    anchor.DepthUnit,
                    [anchor.SegmentId]));
            }
        }

        return diagnostics
            .GroupBy(diagnostic => $"{diagnostic.Code}|{diagnostic.EntityId}|{diagnostic.Depth}")
            .Select(group => group.First())
            .OrderBy(diagnostic => diagnostic.Depth ?? double.MinValue)
            .ThenBy(diagnostic => diagnostic.Code, StringComparer.Ordinal)
            .ToList();
    }

    private static HashSet<string> ExcludedTargets(ProjectSnapshot snapshot)
    {
        var excluded = snapshot.Exclusions.Where(exclusion => exclusion.Active).Select(exclusion => exclusion.TargetId).ToHashSet(StringComparer.Ordinal);
        foreach (var decision in snapshot.Decisions.OrderBy(decision => decision.Sequence))
        {
            if (decision.Kind is "anchor-review" or "exclusion")
            {
                if (decision.Status == "rejected")
                {
                    excluded.Add(decision.TargetId);
                }
                else if (decision.Status == "accepted")
                {
                    excluded.Remove(decision.TargetId);
                }
            }
        }
        return excluded;
    }

    private static List<List<AgeAnchor>>? ExpandInversionChoices(
        List<AgeAnchor> anchors,
        CoreSegment segment,
        List<Diagnostic> diagnostics,
        int remainingBudget)
    {
        var ordered = anchors.OrderBy(anchor => anchor.Depth).ThenBy(anchor => anchor.AnchorId, StringComparer.Ordinal).ToList();
        var conflicting = new HashSet<string>(StringComparer.Ordinal);
        for (var index = 1; index < ordered.Count; index++)
        {
            var previous = Median(ordered[index - 1]);
            var current = Median(ordered[index]);
            if (current <= previous)
            {
                conflicting.Add(ordered[index - 1].AnchorId);
                conflicting.Add(ordered[index].AnchorId);
            }
        }

        if (conflicting.Count == 0)
        {
            return [ordered];
        }

        diagnostics.Add(new Diagnostic(
            "inverted-layer-segment",
            "error",
            "年龄随深度减小的倒置层段不能直接进入单调模型；每个排除选择保留为候选。",
            string.Join(",", conflicting.Order(StringComparer.Ordinal)),
            ordered.First(anchor => conflicting.Contains(anchor.AnchorId)).Depth,
            segment.Unit,
            [segment.SegmentId]));

        if (conflicting.Count > remainingBudget)
        {
            return null;
        }

        return conflicting.Order(StringComparer.Ordinal)
            .Select(anchorId => ordered.Where(anchor => anchor.AnchorId != anchorId).ToList())
            .Where(choice => IsMonotonic(choice))
            .ToList();
    }

    private static bool IsMonotonic(IReadOnlyList<AgeAnchor> anchors)
    {
        for (var index = 1; index < anchors.Count; index++)
        {
            if (Median(anchors[index]) <= Median(anchors[index - 1]))
            {
                return false;
            }
        }
        return true;
    }

    private static ModelCandidate CreateCandidate(CoreSegment segment, List<AgeAnchor> anchors, string parameterVersion)
    {
        var rates = new List<RateInterval>();
        var uncertainty = new List<UncertaintyPoint>();
        for (var index = 0; index < anchors.Count; index++)
        {
            var anchor = anchors[index];
            var sorted = anchor.AgeDistributionKaBp.Order().ToArray();
            uncertainty.Add(new UncertaintyPoint(
                $"unc:{anchor.AnchorId}",
                anchor.AnchorId,
                anchor.Depth,
                Quantile(sorted, 0.05) - anchor.ReservoirOffsetKa,
                Quantile(sorted, 0.50) - anchor.ReservoirOffsetKa,
                Quantile(sorted, 0.95) - anchor.ReservoirOffsetKa,
                "ka BP",
                anchor.DepthUnit));

            if (index == 0)
            {
                continue;
            }
            var previous = anchors[index - 1];
            var ageDelta = Median(anchor) - anchor.ReservoirOffsetKa - (Median(previous) - previous.ReservoirOffsetKa);
            var depthDelta = anchor.Depth - previous.Depth;
            rates.Add(new RateInterval(
                $"rate:{previous.AnchorId}->{anchor.AnchorId}",
                segment.SegmentId,
                previous.Depth,
                anchor.Depth,
                Math.Round(ageDelta / depthDelta, 6),
                "ka/m",
                previous.AnchorId,
                anchor.AnchorId));
        }

        var anchorIds = anchors.Select(anchor => anchor.AnchorId).ToArray();
        var tailPenalty = anchors.Average(anchor =>
        {
            var sorted = anchor.AgeDistributionKaBp.Order().ToArray();
            return Quantile(sorted, 0.95) / Math.Max(Quantile(sorted, 0.50), 0.000001);
        });
        var fitScore = Math.Round(1.0 / tailPenalty, 6);
        var candidateId = "cand-" + Json.StableHash(new
        {
            segment.SegmentId,
            anchorIds,
            parameterVersion
        })[..16];

        return new ModelCandidate(
            candidateId,
            segment.SegmentId,
            anchorIds,
            rates,
            uncertainty,
            fitScore,
            "仅在显式边界内的不断层段强制单调；并列锚点和倒置修正均保留为并列候选。");
    }

    private static double Median(AgeAnchor anchor) => Quantile(anchor.AgeDistributionKaBp.Order().ToArray(), 0.50) - anchor.ReservoirOffsetKa;

    private static double Quantile(IReadOnlyList<double> sorted, double probability)
    {
        if (sorted.Count == 0)
        {
            return double.NaN;
        }
        if (sorted.Count == 1)
        {
            return sorted[0];
        }
        var position = (sorted.Count - 1) * probability;
        var lower = (int)Math.Floor(position);
        var upper = (int)Math.Ceiling(position);
        if (lower == upper)
        {
            return sorted[lower];
        }
        return sorted[lower] + (sorted[upper] - sorted[lower]) * (position - lower);
    }
}
