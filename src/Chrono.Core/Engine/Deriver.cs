using Chrono.Core.Model;
using Chrono.Core.Sourcing;

namespace Chrono.Core.Engine;

/// <summary>
/// The server-side derivation engine. Business rules live here (and are exercised directly by tests),
/// never only in the browser. Emits structured findings for the four governing principles:
/// monotonicity is scoped to continuous runs, gaps/unconformities are explicit boundaries, a single
/// anchor's distribution cannot be replaced by its mean, and all equal-optimal candidates survive.
/// </summary>
public sealed class Deriver
{
    public const string RuleVersion = "rules-2026.09";
    private const double EqualTolerance = 1e-9;

    public DeriveReport Derive(ProjectState state, string branchId, string? asOfEventId = null)
    {
        var reportId = "report_" + StableId.Sha1("rep",
            $"{state.ProjectId}|{branchId}|{state.MaterialsVersion}|{state.Parameters.Version}|{asOfEventId ?? "head"}")[..18];
        var allDiags = new List<Diagnostic>();
        var findings = new List<RuleFindingView>();
        var parents = state.AppliedEvents.TakeLast(6).Select(e => e.Id).ToList();
        long budget = state.Parameters.BudgetDraws;
        long used = 0;

        var build = RunBuilder.Build(state);
        allDiags.AddRange(build.Diagnostics);

        var runs = new List<RunModel>();
        bool budgetDead = false;
        FailureKind? failure = null;

        // Principle: distribution is never reduced to mean — always true while anchors carry distributions.
        findings.Add(new RuleFindingView("DISTRIBUTION_NOT_MEAN", "prepared-anchors", true,
            "anchors are sampled as distributions; quantile bands and reservoir convolution preserve spread",
            build.Prepared.Select(p => p.Anchor.Id).ToList(), reportId));

        // Principle: explicit boundaries
        var boundaryAnchors = build.Runs
            .Where(r => r.BoundedByGap)
            .SelectMany(r => r.Anchors.Select(a => a.Anchor.Id)).Distinct().ToList();
        if (boundaryAnchors.Any())
            findings.Add(new RuleFindingView("BOUNDARY_EXPLICIT", "segment-gaps", true,
                "gaps and unconformities split runs; no interpolation or monotonicity crosses them",
                boundaryAnchors, reportId));

        for (int ri = 0; ri < build.Runs.Count; ri++)
        {
            var spec = build.Runs[ri];
            var runDiags = new List<Diagnostic>();
            var candidates = new List<CandidateModel>();

            // unresolved duplicate depth group (multiple anchors, no human resolution)
            var dupGroups = spec.Anchors.GroupBy(p => Math.Round(p.DepthMetres, 9)).Where(g => g.Count() > 1).ToList();
            bool unresolvedDup = false;
            foreach (var g in dupGroups)
            {
                var resolved = state.DuplicateResolutions.Any(r => Math.Abs(r.DepthMetres - g.Key) < 1e-9);
                var ids = g.Select(p => p.Anchor.Id).OrderBy(x => x, StringComparer.Ordinal).ToList();
                runDiags.Add(new Diagnostic(resolved ? "depth.duplicate.resolved" : "depth.duplicate",
                    resolved ? Severity.Info : Severity.Error,
                    $"run:{spec.Id}",
                    $"depth {g.Key:R} m shared by anchors {string.Join(",", ids)}"
                    + (resolved ? " (resolution recorded)" : " (needs human decision: alternate or drop)"),
                    new Dictionary<string, string>
                    {
                        ["depthMetres"] = g.Key.ToString("R"),
                        ["anchorIds"] = string.Join(",", ids),
                    }));
                if (!resolved) unresolvedDup = true;
            }

            // inversion among depth-ordered anchors (ages younger down-core within the run)
            var byDepth = spec.Anchors.OrderBy(p => p.DepthMetres)
                .ThenBy(p => p.Anchor.Id, StringComparer.Ordinal).ToList();
            bool inverted = false;
            for (int j = 0; j < byDepth.Count - 1; j++)
            {
                if (byDepth[j + 1].DepthMetres - byDepth[j].DepthMetres < 1e-9) continue;
                bool youngerBelow = byDepth[j + 1].Calibrated.Q95 < byDepth[j].Calibrated.Q05;
                bool overlaps = byDepth[j + 1].Calibrated.Q05 <= byDepth[j].Calibrated.Q95;
                if (youngerBelow)
                {
                    inverted = true;
                    runDiags.Add(new Diagnostic("age.inversion", Severity.Error,
                        $"run:{spec.Id}",
                        $"anchor {byDepth[j + 1].Anchor.Id} is confidently younger than {byDepth[j].Anchor.Id} down-core",
                        new Dictionary<string, string>
                        {
                            ["upper"] = byDepth[j].Anchor.Id, ["lower"] = byDepth[j + 1].Anchor.Id,
                            ["upperQ05"] = byDepth[j].Calibrated.Q05.ToString("R"),
                            ["lowerQ95"] = byDepth[j + 1].Calibrated.Q95.ToString("R"),
                        }));
                }
                else if (!overlaps)
                {
                    runDiags.Add(new Diagnostic("age.tight-monotonic", Severity.Info, $"run:{spec.Id}",
                        $"anchors {byDepth[j].Anchor.Id}->{byDepth[j+1].Anchor.Id} confidently older down-core"));
                }
            }

            if (byDepth.Count > 0)
                findings.Add(new RuleFindingView("MONOTONIC_SCOPE", spec.Id, !inverted,
                    inverted
                        ? "age inversion inside a continuous run; monotonicity cannot hold until an explicit boundary/decision is added"
                        : "age ordering is monotonic within the continuous run; gap boundaries excluded from this judgement",
                    byDepth.Select(p => p.Anchor.Id).ToList(), reportId));

            bool blocked = unresolvedDup || inverted || spec.Anchors.Count == 0;
            string? blockReason =
                unresolvedDup ? "unresolved duplicate depth" :
                inverted ? "inverted section inside continuous run" :
                spec.Anchors.Count == 0 ? "no usable anchors in run" : null;

            if (spec.Anchors.Count == 0)
                runDiags.Add(new Diagnostic("run.no-anchor", Severity.Error, $"run:{spec.Id}",
                    "run covers a depth interval without any usable age anchor"));

            if (!blocked)
            {
                long seedBase = unchecked((long)StableHash(reportId + spec.Id));
                foreach (var (tieKey, choice) in CandidateEngine.EnumerateChoices(spec.Anchors))
                {
                    foreach (var strategy in state.Parameters.Strategies)
                    {
                        if (used >= budget) { budgetDead = true; break; }
                        var built = CandidateEngine.Build(spec.Id, strategy, tieKey, choice,
                            state.Parameters.Samples, state.Parameters.DepthGridPoints,
                            state.Parameters.Version, state.MaterialsVersion, seedBase, budget - used);
                        used += built.DrawsUsed;
                        if (built.Model is null)
                        {
                            if (built.Diagnostics.Any(d => d.Code == "budget.exhausted")) budgetDead = true;
                            runDiags.AddRange(built.Diagnostics);
                        }
                        else candidates.Add(built.Model);
                    }
                    if (budgetDead) break;
                }

                if (!budgetDead && candidates.Count > 0)
                {
                    double best = candidates.Min(c => c.Score);
                    foreach (var c in candidates)
                        if (c.Score <= best + EqualTolerance)
                            candidates[candidates.IndexOf(c)] = c with { EqualOptimal = true };
                    var equalOptimal = candidates.Where(c => c.EqualOptimal)
                        .OrderBy(c => c.Score).ThenBy(c => c.Id, StringComparer.Ordinal).ToList();

                    // Principle: multiple equal-optimal models must be retained
                    findings.Add(new RuleFindingView("EQUAL_OPTIMAL_RETAINED", spec.Id, true,
                        equalOptimal.Count > 1
                            ? $"{equalOptimal.Count} equal-optimal candidates retained (score={best:R}); no arbitrary tie-break to one"
                            : "single best candidate within tolerance; still generated alongside alternatives for review",
                        equalOptimal.Select(c => c.Id).ToList(), reportId));

                    candidates = candidates.OrderBy(c => c.Score).ThenBy(c => c.Id, StringComparer.Ordinal).ToList();
                    runs.Add(new RunModel(spec.Id, spec.TopMetres, spec.BottomMetres, spec.BoundedByGap,
                        spec.BoundaryEventIds, choiceOrdered(spec), candidates, equalOptimal, runDiags, false, null));
                    continue;
                }
            }

            runs.Add(new RunModel(spec.Id, spec.TopMetres, spec.BottomMetres, spec.BoundedByGap,
                spec.BoundaryEventIds, byDepth, Array.Empty<CandidateModel>(), Array.Empty<CandidateModel>(),
                runDiags, blocked || budgetDead, budgetDead ? "resource budget exhausted" : blockReason));
            if (budgetDead) failure = FailureKind.BudgetExhausted;
            else if (blocked) failure = FailureKind.SemanticUnresolvable;
        }

        if (budgetDead)
            allDiags.Add(new Diagnostic("budget.exhausted", Severity.Error, "derive",
                $"Monte Carlo budget {budget} draws exhausted before all candidates were built"));

        bool semantic = runs.Any(r => r.SemanticBlocked && r.BlockReason != "resource budget exhausted");
        var reportFailure = failure ?? (semantic ? FailureKind.SemanticUnresolvable : null);
        if (semantic && failure is null)
            allDiags.Add(new Diagnostic("semantic.unresolvable", Severity.Error, "derive",
                "at least one run has no legal model without an explicit human decision"));

        var inputSummary = $"materials=v{state.MaterialsVersion};params=v{state.Parameters.Version};" +
                           $"anchors={build.Prepared.Count};segments={state.Segments.Count}";

        return new DeriveReport(reportId, state.ProjectId, branchId, asOfEventId, RuleVersion,
            state.Parameters.Version, state.MaterialsVersion, inputSummary, parents,
            allDiags.Concat(runs.SelectMany(r => r.Diagnostics)).ToList(),
            runs, findings, reportFailure, used);
    }

    private static IReadOnlyList<PreparedAnchor> choiceOrdered(RunSpec spec)
        => spec.Anchors.OrderBy(p => p.DepthMetres).ThenBy(p => p.Anchor.Id, StringComparer.Ordinal).ToList();

    internal static ulong StableHash(string s)
    {
        ulong h = 1469598103934665603UL;
        foreach (var c in s) { h ^= c; h *= 1099511628211UL; }
        return h;
    }
}
