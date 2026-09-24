using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace AgeModel.Core;

public sealed partial class ChronologyEngine
{
    private static Diagnostic Diagnostic(string code, string severity, string path, string? subjectId, string message, string? raw = null) =>
        new(code, severity, path, subjectId, message, raw);

    private static string HashState(ProjectState state)
    {
        var material = new
        {
            state.Package.CaseId,
            state.Package.DepthDatum,
            anchors = state.Anchors.Values.OrderBy(a => a.Id).Select(a => new { a.Id, a.Depth.Raw, a.Depth.Unit, dist = a.AgeDistribution.RawRepresentation, samples = a.AgeDistribution.Samples }),
            exclusions = state.Exclusions.Values.OrderBy(e => e.Id),
            parameters = state.Parameters,
            correlations = state.AcceptedCorrelations.OrderBy(kv => kv.Key)
        };
        return ProjectStateFactory.DeterministicHash(material);
    }

    private static string StableHash(IEnumerable<string> anchors, string ruleVersion, string caseId)
    {
        var text = caseId + "|" + ruleVersion + "|" + string.Join(",", anchors.OrderBy(StringComparer.Ordinal));
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text))).ToLowerInvariant();
    }

    private static IReadOnlyList<AnchorView> NormalizeAnchors(ProjectState state, List<Diagnostic> diagnostics)
    {
        var result = new List<AnchorView>();
        foreach (var anchor in state.Package.Anchors)
        {
            var depth = QuantityNormalizer.NormalizeDepth(anchor.Depth, state.Package.DepthDatum.DepthUnit, state.Package.DepthDatum.CoordinateDatum);
            if (!depth.IsConvertible)
            {
                diagnostics.Add(Diagnostic("UNCONVERTIBLE_DEPTH", "error", $"anchors[{anchor.Id}].depth", anchor.Id,
                    depth.Diagnostic ?? "Depth cannot be normalized.", anchor.Depth.Raw + " " + anchor.Depth.Unit));
            }

            var distribution = AdjustReservoir(anchor, state, diagnostics);
            var median = Quantile(distribution.Samples, 0.50);
            var q01 = Quantile(distribution.Samples, 0.01);
            var q05 = Quantile(distribution.Samples, 0.05);
            var q95 = Quantile(distribution.Samples, 0.95);
            var q99 = Quantile(distribution.Samples, 0.99);
            if (median is null || q01 is null)
            {
                diagnostics.Add(Diagnostic("DISTRIBUTION_MISSING", "error", $"anchors[{anchor.Id}].ageDistribution", anchor.Id,
                    "An empirical distribution requires numeric samples; a mean is not an acceptable replacement."));
                continue;
            }

            var segment = state.Package.Segments.FirstOrDefault(s => depth.NormalizedValue is double d && InClosedSegment(d, s));
            var exclusion = state.Exclusions.Values
                .Where(e => e.SubjectType == "anchor" && e.SubjectId == anchor.Id)
                .OrderByDescending(e => e.AtRuleVersion).FirstOrDefault();
            var used = depth.IsConvertible && exclusion?.Status != "excluded";
            result.Add(new AnchorView(anchor, segment?.Id ?? "outside-segments", depth.NormalizedValue!.Value,
                median.Value, q01!.Value, q05!.Value, q95!.Value, q99!.Value, used,
                exclusion?.Status == "excluded" ? exclusion.Reason : null));
        }
        return result;
    }

    private static EmpiricalDistribution AdjustReservoir(AgeAnchor anchor, ProjectState state, List<Diagnostic> diagnostics)
    {
        if (string.IsNullOrEmpty(anchor.ReservoirOffsetId)) return anchor.AgeDistribution;
        if (!state.Offsets.TryGetValue(anchor.ReservoirOffsetId, out var offset))
        {
            diagnostics.Add(Diagnostic("RESERVOIR_OFFSET_MISSING", "error",
                $"anchors[{anchor.Id}].reservoirOffsetId", anchor.Id, "Reservoir offset reference does not resolve."));
            return anchor.AgeDistribution;
        }

        var normalized = QuantityNormalizer.NormalizeAge(offset.Offset);
        if (!normalized.IsConvertible || !normalized.NormalizedValue.HasValue)
        {
            diagnostics.Add(Diagnostic("UNCONVERTIBLE_RESERVOIR_OFFSET", "warning",
                $"reservoirOffsets[{offset.Id}].offset", offset.Id,
                normalized.Diagnostic ?? "Reservoir offset cannot be normalized; uncorrected distribution retained.",
                offset.Offset.Raw + " " + offset.Offset.Unit));
            return anchor.AgeDistribution;
        }

        var policyApplies = state.Parameters.ReservoirPolicy switch
        {
            "apply-to-radiocarbon" => anchor.Method.Equals("radiocarbon", StringComparison.OrdinalIgnoreCase),
            "apply-when-linked" => true,
            "none" => false,
            _ => true
        };
        if (!policyApplies) return anchor.AgeDistribution;

        var shift = normalized.NormalizedValue.Value;
        var shifted = anchor.AgeDistribution.Samples.Select(s => s - shift).ToArray();
        return anchor.AgeDistribution with
        {
            Samples = shifted,
            RawRepresentation = anchor.AgeDistribution.RawRepresentation + " − reservoir(" + offset.Id + "=" + offset.Offset.Raw + " " + offset.Offset.Unit + ")"
        };
    }

    private static double? Quantile(IReadOnlyList<double> sortedOrUnsorted, double probability)
    {
        if (sortedOrUnsorted.Count == 0) return null;
        var sorted = sortedOrUnsorted.OrderBy(v => v).ToArray();
        var position = (sorted.Length - 1) * probability;
        var lower = (int)Math.Floor(position);
        var upper = (int)Math.Ceiling(position);
        if (lower == upper) return sorted[lower];
        var weight = position - lower;
        return sorted[lower] * (1.0 - weight) + sorted[upper] * weight;
    }

    private static IEnumerable<List<AnchorView>> EnumerateCombinations(IReadOnlyList<List<AnchorView>> alternatives)
    {
        var counts = alternatives.Select(a => a.Count).ToArray();
        var indices = new int[alternatives.Count];
        var total = 1L;
        foreach (var count in counts)
        {
            checked
            {
                total *= count;
            }
            if (total > int.MaxValue) yield break;
        }

        for (long i = 0; i < total; i++)
        {
            if (!candidateBudget.TryConsume())
                yield break;
            var current = i;
            for (var j = 0; j < alternatives.Count; j++)
            {
                indices[j] = (int)(current % counts[j]);
                current /= counts[j];
            }
            yield return Enumerable.Range(0, alternatives.Count).Select(j => alternatives[j][indices[j]]).ToList();
        }
    }

    private static bool InClosedSegment(double depth, CoreSegment segment) =>
        segment.StartDepth.NormalizedValue is double start && segment.EndDepth.NormalizedValue is double end
            ? depth >= start - 1e-9 && depth <= end + 1e-9
            : false;

    private static StratigraphicBoundary? CrossingBoundary(double topDepth, double baseDepth, ProjectState state)
    {
        foreach (var boundary in state.Package.Boundaries)
        {
            if (boundary.AllowsAgeInterpolation) continue;
            if (boundary.TopDepth.NormalizedValue is double top && boundary.BaseDepth.NormalizedValue is double bottom)
            {
                var topPoint = Math.Min(top, bottom);
                var bottomPoint = Math.Max(top, bottom);
                if (topDepth < bottomPoint - 1e-9 && baseDepth > topPoint + 1e-9)
                    return boundary;
            }
        }
        return null;
    }

    private static IEnumerable<string> CheckCorrelations(IReadOnlyList<AnchorView> ordered, ProjectState state)
    {
        foreach (var layer in state.Events.Values)
        {
            var isAccepted = state.AcceptedCorrelations.TryGetValue(layer.CorrelationId, out var acceptedLayerId) &&
                             acceptedLayerId == layer.Id;
            if (!isAccepted) continue;
            var linked = ordered.Where(a => layer.LinkedAnchorIds.Contains(a.Anchor.Id)).ToList();
            if (linked.Count < 2) continue;
            var low = linked.Max(a => a.Q05);
            var high = linked.Min(a => a.Q95);
            if (high < low)
            {
                yield return $"Event correlation {layer.CorrelationId} requires overlapping 90% distributions among linked anchors; {string.Join(",", linked.Select(a => a.Anchor.Id))} do not overlap.";
            }
        }
    }
