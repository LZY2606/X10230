using System.Globalization;
using System.Text.Json;

namespace AgeModel.Core;

public sealed partial class ChronologyEngine
{
    private CandidateBudget candidateBudget = new(1024);

    public EngineResult Evaluate(ProjectState state, int? candidateBudgetOverride = null, string? probeId = null)
    {
        candidateBudget = new CandidateBudget(candidateBudgetOverride ?? state.Parameters.MaxCandidates);
        var diagnostics = new List<Diagnostic>();
        var anchors = NormalizeAnchors(state, diagnostics);
        var byDepth = anchors
            .GroupBy(a => a.DepthM)
            .OrderBy(group => group.Key)
            .ToList();

        foreach (var group in byDepth.GroupBy(g => g.Key).Where(g => g.Count() > 1))
        {
            diagnostics.Add(Diagnostic("DEPTH_DUPLICATE", "warning",
                $"anchors[?].depth:{group.Key.ToString(CultureInfo.InvariantCulture)}", null,
                $"{group.Count()} anchors occupy exactly the same depth and are treated as alternatives."));
        }

        var inclusionDiagnostics = ValidateInclusions(state, anchors);
        diagnostics.AddRange(inclusionDiagnostics);
        if (inclusionDiagnostics.Any(d => d.Severity == "error"))
        {
            return new EngineResult(ProjectStatus.SemanticallyUnsolvable, HashState(state), state.Parameters,
                diagnostics, Array.Empty<AnchorView>(), Array.Empty<CandidateModel>(),
                Array.Empty<RejectedModel>(), Array.Empty<RateIntervalView>());
        }

        var available = anchors.Where(a => a.Used).OrderBy(a => a.DepthM).ThenBy(a => a.Anchor.Id, StringComparer.Ordinal).ToList();
        var alternatives = available.GroupBy(a => a.DepthM).Select(g => g.OrderBy(a => a.Anchor.Id, StringComparer.Ordinal).ToList()).ToList();
        var rejected = new List<RejectedModel>();
        var valid = new List<CandidateModel>();
        var exhausted = false;

        foreach (var combination in EnumerateCombinations(alternatives))
        {
            if (!candidateBudget.TryConsume())
            {
                exhausted = true;
                break;
            }

            var ordered = combination.OrderBy(a => a.DepthM).ThenBy(a => a.Anchor.Id, StringComparer.Ordinal).ToList();
            var reasons = new List<string>();
            foreach (var segment in state.Package.Segments)
            {
                var inSegment = ordered.Where(a => InClosedSegment(a.DepthM, segment)).ToList();
                if (inSegment.Count < state.Parameters.MinimumAnchorsPerInterval)
                {
                    reasons.Add($"Segment {segment.Id} has {inSegment.Count} anchors but requires {state.Parameters.MinimumAnchorsPerInterval}.");
                }
            }

            foreach (var pair in ordered.Zip(ordered.Skip(1), (a, b) => (a, b)))
            {
                var boundary = CrossingBoundary(pair.a.DepthM, pair.b.DepthM, state);
                if (boundary is not null)
                {
                    reasons.Add($"Anchors {pair.a.Anchor.Id} and {pair.b.Anchor.Id} cross explicit boundary {boundary.Id}; interpolation is forbidden.");
                    continue;
                }

                if (pair.b.MedianAge < pair.a.MedianAge - 1e-9 && state.Parameters.MonotonicDirection == "older-with-depth")
                {
                    reasons.Add($"Inverted ages between {pair.a.Anchor.Id} ({pair.a.MedianAge.ToString(CultureInfo.InvariantCulture)}) and {pair.b.Anchor.Id} ({pair.b.MedianAge.ToString(CultureInfo.InvariantCulture)}).");
                }
            }

            var correlationReason = CheckCorrelations(ordered, state);
            reasons.AddRange(correlationReason);
            var included = ordered.Select(a => a.Anchor.Id).ToArray();
            var hash = StableHash(included, state.Parameters.Version, state.Package.CaseId);
            if (reasons.Count > 0)
            {
                rejected.Add(new RejectedModel($"rejected-{hash[..12]}", hash, included, reasons,
                    reasons.Any(r => r.StartsWith("Inverted", StringComparison.Ordinal)) ? "INVERTED_SEGMENT" : "CONSTRAINT_VIOLATION",
                    probeId ?? "baseline"));
                continue;
            }

            var candidate = BuildCandidate(state, ordered, hash, valid.Count, diagnostics);
            valid.Add(candidate);
        }

        if (exhausted)
        {
            diagnostics.Add(Diagnostic("RESOURCE_BUDGET_EXHAUSTED", "error", "parameters.maxCandidates", null,
                "Candidate enumeration reached its deterministic resource budget before completion."));
            return new EngineResult(ProjectStatus.ResourceBudgetExhausted, HashState(state), state.Parameters,
                diagnostics, anchors, valid, rejected, Array.Empty<RateIntervalView>());
        }

        if (valid.Count == 0)
        {
            diagnostics.Add(Diagnostic("SEMANTIC_NO_SOLUTION", "error", "candidates", null,
                "No complete age model satisfies segment, boundary, monotonic and event-correlation constraints."));
            return new EngineResult(ProjectStatus.SemanticallyUnsolvable, HashState(state), state.Parameters,
                diagnostics, anchors, Array.Empty<CandidateModel>(), rejected, Array.Empty<RateIntervalView>());
        }

        var ranked = RankAndAdopt(valid, state);
        return new EngineResult(ProjectStatus.Ok, HashState(state), state.Parameters, diagnostics, anchors,
            ranked, rejected, Array.Empty<RateIntervalView>());
    }
