using ChronoCore.Domain;

namespace ChronoCore.Core;

/// <summary>Inputs to normalization: the immutable material layer plus decisions in force.</summary>
public sealed record NormalizeInput(
    string CoreId,
    DepthDatum Datum,
    IReadOnlyList<SegmentMaterial> Segments,
    IReadOnlyList<AnchorMaterial> Anchors,
    IReadOnlyList<ReservoirOffsetSpec> ReservoirOffsets,
    IReadOnlyList<EventBedMaterial> EventBeds,
    IReadOnlyList<BoundaryMaterial> Boundaries,
    IReadOnlyList<ExclusionDecision> Exclusions);

/// <summary>
/// Normalizes materials into canonical m / yr BP. Values that cannot be converted stay in place
/// (the material is retained) and get a located diagnostic — they are never dropped for plotting.
/// </summary>
public static class Normalizer
{
    public static NormalizedProfile Normalize(NormalizeInput input, RuleSet rules)
    {
        var diags = new List<Diagnostic>();
        var excluded = ActiveExclusions(input.Exclusions);

        var segments = new List<NormalizedSegment>();
        foreach (var s in input.Segments)
        {
            if (excluded.Contains(s.Id)) continue;
            if (!Units.TryConvertDepth(s.Top, input.Datum.Name, out var topM, out var r1))
            {
                diags.Add(new Diagnostic("unconvertible-depth", "error", $"segments[{s.Id}].top", r1!, s.Id));
                continue;
            }
            if (!Units.TryConvertDepth(s.Bottom, input.Datum.Name, out var botM, out var r2))
            {
                diags.Add(new Diagnostic("unconvertible-depth", "error", $"segments[{s.Id}].bottom", r2!, s.Id));
                continue;
            }
            if (topM > botM + 1e-9)
            {
                diags.Add(new Diagnostic("inverted-segment", "error",
                    $"segments[{s.Id}]", $"segment {s.Id} top {topM:F3} m is deeper than bottom {botM:F3} m", s.Id));
                continue;
            }
            segments.Add(new NormalizedSegment(s.Id, topM, botM, s.Facies));
        }

        var boundaries = new List<NormalizedBoundary>();
        foreach (var b in input.Boundaries)
        {
            if (excluded.Contains(b.Id)) continue;
            if (!Units.TryConvertDepth(b.Depth, input.Datum.Name, out var dM, out var rb))
            {
                diags.Add(new Diagnostic("unconvertible-depth", "error", $"boundaries[{b.Id}].depth", rb!, b.Id));
                continue;
            }
            boundaries.Add(new NormalizedBoundary(b.Id, dM, b.Kind, b.MissingInterval));
        }
        boundaries = boundaries.OrderBy(b => b.DepthM).ToList();

        var profileBottom = segments.Count == 0
            ? boundaries.DefaultIfEmpty().Max(b => b?.DepthM ?? 0)
            : segments.Max(s => s.BottomM);
        var runs = BuildRuns(boundaries, profileBottom);

        var anchors = new List<NormalizedAnchor>();
        var seenContent = new HashSet<string>();
        foreach (var a in input.Anchors.OrderBy(x => x.Id, StringComparer.Ordinal))
        {
            if (excluded.Contains(a.Id)) continue;
            var contentKey = StableId.Of(a.CoreId, a.Depth.Value, a.Depth.Unit, a.Depth.Datum ?? "",
                a.Age.Kind, a.Age.MedianYrBp, a.Age.SigmaYr ?? 0, a.Method);
            if (!seenContent.Add(contentKey))
            {
                diags.Add(new Diagnostic("duplicate-material", "error",
                    $"anchors[{a.Id}]", $"anchor {a.Id} repeats an earlier anchor with identical evidence; second copy not merged", a.Id));
                continue;
            }
            if (!Units.TryConvertDepth(a.Depth, input.Datum.Name, out var depthM, out var ra))
            {
                diags.Add(new Diagnostic("unconvertible-depth", "error", $"anchors[{a.Id}].depth", ra!, a.Id));
                continue;
            }
            double reservoirYr = 0;
            var offset = input.ReservoirOffsets.FirstOrDefault(o => o.AnchorId == a.Id)
                         ?? input.ReservoirOffsets.FirstOrDefault(o => o.AnchorId is null);
            if (offset is not null)
            {
                if (!Units.TryConvertAge(offset.DeltaAge, out reservoirYr, out var ro))
                {
                    diags.Add(new Diagnostic("unconvertible-offset", "warning",
                        $"anchors[{a.Id}].reservoir", ro!, a.Id));
                    reservoirYr = 0;
                }
            }
            var corrected = ApplyReservoir(a.Age, reservoirYr);
            if (corrected.TailSkew() > rules.LongTailSkewLimit || corrected.SampleCount > rules.MaxSamples)
            {
                diags.Add(new Diagnostic("long-tail-distribution", "warning",
                    $"anchors[{a.Id}].age",
                    $"anchor {a.Id} posterior has a long tail (skew {corrected.TailSkew():F2}, {corrected.SampleCount} samples); mean substitution forbidden",
                    a.Id));
            }
            anchors.Add(new NormalizedAnchor(a.Id, depthM, corrected, reservoirYr));
        }

        var tieGroups = BuildTieGroups(anchors, rules, diags);
        anchors = anchors.OrderBy(a => a.DepthM).ThenBy(a => a.MaterialId, StringComparer.Ordinal).ToList();

        CheckWithinRunMonotonicity(anchors, runs, rules, diags);

        var eventBeds = new List<NormalizedEventBed>();
        foreach (var e in input.EventBeds)
        {
            if (excluded.Contains(e.Id)) continue;
            if (!Units.TryConvertDepth(e.Depth, input.Datum.Name, out var dM, out var re))
            {
                diags.Add(new Diagnostic("unconvertible-depth", "error", $"eventBeds[{e.Id}].depth", re!, e.Id));
                continue;
            }
            eventBeds.Add(new NormalizedEventBed(e.Id, dM, e.Name, e.CorrelatesTo));
        }

        return new NormalizedProfile(input.CoreId, input.Datum.Name,
            segments, anchors, boundaries, eventBeds.OrderBy(e => e.DepthM).ToList(),
            runs, tieGroups, diags);
    }

    private static HashSet<string> ActiveExclusions(IReadOnlyList<ExclusionDecision> decisions)
    {
        var revoked = new HashSet<string>(decisions.Where(d => d.RevokesDecisionId is not null).Select(d => d.RevokesDecisionId!));
        var set = new HashSet<string>();
        foreach (var d in decisions)
        {
            if (d.RevokesDecisionId is not null) continue;
            if (revoked.Contains(d.Id)) continue;
            set.Add(d.TargetId);
        }
        return set;
    }

    private static AgeDistribution ApplyReservoir(AgeDistribution age, double reservoirYr)
    {
        if (reservoirYr == 0) return age;
        // Marine reservoir makes the measured age too old: shift the whole distribution younger.
        return age with
        {
            MedianYrBp = age.MedianYrBp - reservoirYr,
            SigmaYr = age.SigmaYr,
            P05YrBp = age.P05YrBp is { } p05 ? p05 - reservoirYr : null,
            P95YrBp = age.P95YrBp is { } p95 ? p95 - reservoirYr : null,
            Samples = age.Samples?.Select(s => s - reservoirYr).ToList()
        };
    }

    private static List<RunSpan> BuildRuns(IReadOnlyList<NormalizedBoundary> boundaries, double profileBottom)
    {
        var runs = new List<RunSpan>();
        double top = 0;
        var i = 0;
        foreach (var b in boundaries)
        {
            runs.Add(new RunSpan(i, top, b.DepthM));
            top = b.DepthM;
            i++;
        }
        runs.Add(new RunSpan(i, top, Math.Max(top, profileBottom)));
        return runs;
    }

    private static List<TieGroup> BuildTieGroups(List<NormalizedAnchor> anchors, RuleSet rules, List<Diagnostic> diags)
    {
        var groups = new List<TieGroup>();
        var inGroup = new HashSet<string>();
        var tol = rules.TieToleranceM;
        for (var i = 0; i < anchors.Count; i++)
        {
            if (inGroup.Contains(anchors[i].MaterialId)) continue;
            var cluster = anchors.Where(a => Math.Abs(a.DepthM - anchors[i].DepthM) <= tol).ToList();
            if (cluster.Count <= 1) continue;
            foreach (var c in cluster) inGroup.Add(c.MaterialId);
            var ids = cluster.Select(c => c.MaterialId).OrderBy(x => x, StringComparer.Ordinal).ToList();
            var key = "tie@" + anchors[i].DepthM.ToString("F3");
            var lo = cluster.Max(a => a.Age.EffectiveP05);
            var hi = cluster.Min(a => a.Age.EffectiveP95);
            string resolution;
            if (lo > hi + rules.MonotonicToleranceYr)
            {
                resolution = "unresolvable";
                diags.Add(new Diagnostic("tie-unresolvable", "error",
                    $"anchors[{string.Join(',', ids)}]",
                    $"tied anchors at {anchors[i].DepthM:F3} m have disjoint age intervals [{lo:F0},{cluster.Min(a => a.Age.EffectiveP95):F0}]"));
            }
            else
            {
                resolution = "intersect";
                diags.Add(new Diagnostic("tied-anchors", "warning",
                    $"anchors[{string.Join(',', ids)}]",
                    $"tied anchors at {anchors[i].DepthM:F3} m resolved by interval intersection, both distributions retained"));
            }
            groups.Add(new TieGroup(key, anchors[i].DepthM, ids, resolution));
        }
        return groups;
    }

    private static void CheckWithinRunMonotonicity(List<NormalizedAnchor> anchors, IReadOnlyList<RunSpan> runs,
        RuleSet rules, List<Diagnostic> diags)
    {
        foreach (var run in runs)
        {
            var inRun = anchors.Where(a => a.DepthM >= run.TopM - 1e-9 && a.DepthM <= run.BottomM + 1e-9)
                .OrderBy(a => a.DepthM).ToList();
            for (var i = 1; i < inRun.Count; i++)
            {
                var upper = inRun[i - 1];
                var lower = inRun[i];
                if (Math.Abs(upper.DepthM - lower.DepthM) <= rules.TieToleranceM) continue;
                // With depth, ages must get older (larger yr BP). Disjoint inversion = no legal model.
                if (upper.Age.EffectiveP05 > lower.Age.EffectiveP95 + rules.MonotonicToleranceYr)
                {
                    diags.Add(new Diagnostic("inverted-ages-disjoint", "error",
                        $"anchors[{lower.MaterialId}]",
                        $"run {run.RunIndex}: anchor {upper.MaterialId} ({upper.DepthM:F3} m) is older than {lower.MaterialId} ({lower.DepthM:F3} m) with disjoint distributions",
                        lower.MaterialId));
                }
            }
        }
    }
}
