using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using Chrono.Core.Events;

namespace Chrono.Core.Model;

/// <summary>
/// Server-side rule engine. Takes reconstructed state + triggering event and returns a fully
/// structured, replayable derivation: input summary, parameter version, parent dependencies,
/// machine-readable judgments and all defensible candidates.
/// </summary>
public sealed class ModelEngine
{
    private static readonly JsonSerializerOptions JsonOpts = new(JsonSerializerDefaults.Web);

    public DerivationResult Derive(ProjectState state, string branch, long triggerSeq,
        IReadOnlyList<string> parentEventIds)
    {
        var builder = new TrackBuilder();
        var build = builder.Build(state);
        var engine = new CandidateEngine();
        var trackResults = new List<TrackResult>();
        var judgments = new List<RuleJudgment>();
        var diagnostics = new List<string>(build.Diagnostics.Select(d => d.ToString()));

        foreach (var track in build.Tracks)
        {
            var cands = engine.Enumerate(track, state.Rules, build.DuplicateDepthAnchors);
            MarkEquallyOptimal(cands);

            var trackDiags = new List<string>();
            string status;
            if (cands.Count == 0) { status = "NO_CANDIDATE"; }
            else if (cands.Any(c => c.Feasible))
                status = cands.Count(c => c.Feasible && c.EquallyOptimal) > 1 ? "MULTIPLE_EQUALLY_GOOD" : "OK";
            else if (cands.Any(c => c.Diagnostics.Any(x => x.StartsWith("UNDERCONSTRAINED"))))
                status = "UNDERCONSTRAINED";
            else
                status = "SEMANTIC_NO_SOLUTION";

            if (track.BoundaryMaterialIds.Count > 0)
            {
                judgments.Add(new RuleJudgment(
                    RuleJudgment.GapIsExplicitBoundary, "track_boundary", "boundary",
                    "depth gap / unconformity / fault splits age-depth tracks; no interpolation across it",
                    new[] { track.Id }, string.Join(",", track.BoundaryMaterialIds)));
            }
            judgments.Add(new RuleJudgment(
                RuleJudgment.MonotoneOnlyWithinContinuous, "monotonicity", "enforced_within_track",
                "age increases with depth only inside one continuous track; gaps and faults are explicit boundaries",
                new[] { track.Id }));
            judgments.Add(new RuleJudgment(
                RuleJudgment.DistributionNotMean, "anchor_distributions", "retained",
                "each anchor keeps its full distribution; uncertainty band comes from draw-wise sampling",
                track.Anchors.Select(a => a.Id).ToList()));

            if (cands.Count(c => c is { Feasible: true, EquallyOptimal: true }) > 1)
                judgments.Add(new RuleJudgment(
                    RuleJudgment.KeepAllEquallyGood, "candidate_tie", "all_retained",
                    "multiple candidates tie on the deterministic score; every equally-good model is retained",
                    cands.Where(c => c is { Feasible: true, EquallyOptimal: true }).Select(c => c.Id).ToList()));

            if (engine.Failure == DerivationFailure.BudgetExhausted)
                trackDiags.Add("BUDGET_EXHAUSTED: candidate enumeration reached rule-set cap; partial result only");

            trackResults.Add(new TrackResult(track, status, cands, trackDiags));
        }

        var overall = build.Diagnostics.Count > 0 && build.Tracks.Count == 0
            ? "UNCONVERTIBLE_INPUT_RETAINED"
            : trackResults.Any(t => t.Status == "SEMANTIC_NO_SOLUTION")
                ? "SEMANTIC_NO_SOLUTION"
                : trackResults.All(t => t.Status == "NO_CANDIDATE")
                    ? "NO_CANDIDATE"
                    : "DERIVED";

        if (engine.Failure == DerivationFailure.BudgetExhausted)
            overall = "BUDGET_EXHAUSTED";

        var fingerprint = InputSummary(state, branch, triggerSeq);
        return new DerivationResult(
            DerivationId: $"derivation/{branch}@{triggerSeq}:{CandidateEngine.StableHash(fingerprint):x8}",
            Branch: branch,
            TriggerSeq: triggerSeq,
            RuleVersion: state.Rules.Version,
            InputSummary: fingerprint,
            ParentEventIds: parentEventIds,
            Tracks: trackResults,
            Judgments: judgments,
            Diagnostics: diagnostics,
            Status: overall);
    }

    private static void MarkEquallyOptimal(List<CandidateModel> cands)
    {
        var feasible = cands.Where(c => c.Feasible).ToList();
        if (feasible.Count == 0) return;
        double best = feasible.Min(c => c.Score);
        const double tol = 1e-9;
        var tied = feasible.Where(c => Math.Abs(c.Score - best) <= tol).ToList();
        foreach (var t in tied)
            cands[cands.FindIndex(c => c.Id == t.Id)] = t with { EquallyOptimal = true };
        // stable, equivalent-input-stable ordering: score then identity lexicographic
        cands.Sort((a, b) =>
        {
            int s = a.Score.CompareTo(b.Score);
            return s != 0 ? s : string.CompareOrdinal(a.Id, b.Id);
        });
    }

    public static string InputSummary(ProjectState state, string branch, long seq)
    {
        var mat = state.Materials.Values
            .Where(m => !m.Superseded)
            .OrderBy(m => m.Id, StringComparer.Ordinal)
            .Select(m => new
            {
                m.Id,
                m.Kind,
                m.SourceFile,
                m.Line,
                diags = m.Diagnostics.Select(d => d.Code)
            });
        var summary = new
        {
            project = state.ProjectId,
            core = state.CoreId,
            branch,
            seq,
            rules = state.Rules.Version,
            ruleParams = new { state.Rules.DrawCount, state.Rules.RngSeed, state.Rules.MaxCandidates,
                state.Rules.MinSedRateCmPerYear, state.Rules.MaxSedRateCmPerYear },
            correlations = state.ChosenCorrelations.OrderBy(kv => kv.Key),
            exclusions = state.Exclusions.OrderBy(e => e.Id).Select(e => new { e.Id, e.TargetMaterialId, e.Reason }),
            materials = mat
        };
        return JsonSerializer.Serialize(summary, JsonOpts);
    }
}
