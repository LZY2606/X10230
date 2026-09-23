using Chrono.Core.Engine;
using Chrono.Core.Events;
using Chrono.Core.Materials;
using Chrono.Core.Units;
using Chrono.Storage.Workflow;

namespace Chrono.Storage.CasePack;

public sealed partial class CasePackRunner
{
    private readonly WorkflowService _workflow;
    private readonly ProjectStore _store;

    public CasePackRunner(WorkflowService workflow, ProjectStore store)
    {
        _workflow = workflow;
        _store = store;
    }

    public ReplayReport RunMainline()
    {
        var steps = new List<ReplayStep>();
        _store.Initialize();
        _store.PutProjectMeta(CasePackBuilder.ProjectId, CasePackBuilder.ProjectName,
            DateTimeOffset.UtcNow.ToString("O"));

        var baseline = CasePackBuilder.Baseline();

        // 1. Baseline import: datum, gaps, radiometric distributions, reservoir offset,
        //    event correlations and every exclusion decision are all retained.
        var import = _workflow.CommitMaterials(CasePackBuilder.ProjectId, "main", baseline,
            1, null, null, "sedimentology-team",
            baseline.Anchors.Select(a => a.Id)
                .Concat(baseline.Boundaries.Select(b => b.Id))
                .Concat(baseline.Exclusions.Select(e => e.Id)).ToList(),
            "baseline import (depth datum, gaps, distributions, reservoir, events, exclusions)");
        var importEvent = import.Event!;
        steps.Add(new ReplayStep("step-01-import", "baseline materials imported", "main",
            Array.Empty<string>(), "accepted", importEvent.Id,
            "sections 1H-4H; hiatus 2.50 m; coring gap 4.00 m; 12 anchors; 2 source exclusions; K-Ah and T-3 correlations; reservoir NWP-220",
            "immutable materials committed with raw representations retained",
            new[] { "all-materials" }, new[] { importEvent.Id }));

        // 2. Baseline derivation across the explicit boundaries.
        var baselineCompute = _workflow.Compute(CasePackBuilder.ProjectId, "main",
            BuildBudget.Default, "sedimentology-team", "baseline derivation");
        steps.Add(new ReplayStep("step-02-baseline-derive", "baseline age-depth model derived", "main",
            new[] { "unconformity", "depth-duplicate", "parallel-anchor", "age-distribution-longtail" },
            baselineCompute.Build!.Status.ToString(), baselineCompute.Event!.Id,
            "anchors projected into three chains split at hiatus 2.50 m and coring gap 4.00 m",
            baselineCompute.Build.Report.Summary,
            baselineCompute.Build.Candidates.Select(c => c.CandidateId).ToList(),
            new[] { importEvent.Id, baselineCompute.Event.Id }));

        // 3. Parameter revision (single change of the rule/parameter layer).
        var v2 = CasePackBuilder.RevisedParameters();
        var revise = _workflow.ReviseParameters(CasePackBuilder.ProjectId, "main",
            v2, baselineCompute.Event!.Id, "sedimentology-team",
            "parameter revision: 4000 samples, rate cap 120 cm/ka");
        var computeV2 = _workflow.Compute(CasePackBuilder.ProjectId, "main",
            BuildBudget.Default, "sedimentology-team", "derivation under params v2");
        steps.Add(new ReplayStep("step-03-parameter-revision", "parameter revision applied", "main",
            new[] { "local-recompute" },
            computeV2.Build!.Status.ToString(), revise.Event!.Id,
            "params v1 -> params v2 (MonteCarloSamples 2000 -> 4000; rate cap)",
            "re-derivation recorded with new parameters version and parent dependency",
            computeV2.Build.Candidates.Select(c => c.CandidateId).ToList(),
            new[] { baselineCompute.Event.Id, revise.Event.Id, computeV2.Event!.Id }));

        // 4. Local-only source change: reservoir DeltaR 220 -> 260.
        var revisedMaterials = CasePackBuilder.RevisedMaterialsLocalReservoir();
        var local = _workflow.CommitMaterials(CasePackBuilder.ProjectId, "main",
            revisedMaterials, 2, importEvent.Id, "res-nwp",
            "sedimentology-team",
            new[] { "res-nwp", "a-06", ComputeAffectedChain(computeV2.Build!, "a-06") }
                .Where(x => !string.IsNullOrEmpty(x)).ToList(),
            "local source change: reservoir DeltaR 220 +/- 60 -> 260 +/- 70");
        var computeLocal = _workflow.Compute(CasePackBuilder.ProjectId, "main",
            BuildBudget.Default, "sedimentology-team", "local recompute after reservoir edit");
        var locality = CheckLocality(baselineCompute.Build!, computeLocal.Build!);
        steps.Add(new ReplayStep("step-04-local-source-change", "reservoir source changed (one direct source only)", "main",
            new[] { "local-recompute" }, locality ? "accepted" : "FAILED", local.Event!.Id,
            "only direct source res-nwp changed; anchor a-06 corrected age moved 40 yr older",
            locality
                ? "impact confined to the chain containing a-06; other-chain candidates reproduce identical bands"
                : "impact leaked outside affected chain",
            new[] { "res-nwp", "a-06" },
            new[] { importEvent.Id, local.Event.Id, computeLocal.Event!.Id }));

        // 5. Conflicting human decisions across branches.
        var conflict = AppendConflictingDecisions(steps, computeLocal.Event!.Id);

        // 6. Freeze a model, then prove concurrent append conflict on frozen state.
        var chosen = computeLocal.Build!.Candidates.OrderBy(c => c.Score).First();
        var freeze = _workflow.Freeze(CasePackBuilder.ProjectId, "main",
            chosen.CandidateId, computeLocal.Event.Id, null,
            "frozen baseline of best-ranked candidate for new-anchor comparison",
            "sedimentology-team");
        var outcome1 = _workflow.RecordDecision(CasePackBuilder.ProjectId, "main",
            new DecisionPayload("dec-concurrent-1", DecisionAction.PreferCandidate,
                chosen.CandidateId, "simulated concurrent writer A", null, null),
            freeze.Event!.Id, "writer-a");
        var outcome2 = _workflow.RecordDecision(CasePackBuilder.ProjectId, "main",
            new DecisionPayload("dec-concurrent-2", DecisionAction.PreferCandidate,
                chosen.CandidateId, "simulated concurrent writer B", null, null),
            freeze.Event.Id, "writer-b");
        bool oneAcceptedOneFrozen = outcome1.Failure == WorkflowFailure.BranchFrozen
            && outcome2.Failure == WorkflowFailure.BranchFrozen;
        steps.Add(new ReplayStep("step-06-concurrent-freeze", "model frozen; concurrent writes rejected", "main",
            new[] { "concurrent-freeze" }, oneAcceptedOneFrozen ? "accepted" : "FAILED", freeze.Event.Id,
            $"candidate {chosen.CandidateId} frozen; two writers attempted append at same head",
            oneAcceptedOneFrozen
                ? "branch frozen; both appends refused with BranchFrozen, history stays append-only"
                : "frozen branch accepted a write",
            new[] { chosen.CandidateId },
            new[] { computeLocal.Event.Id, freeze.Event.Id }));

        // 7. Optimistic concurrency without a frozen branch, on the counter branch.
        steps.Add(AppendOptimisticConflict(conflict));

        // 8. Counter-archive branches: three distinct failure classes.
        AppendSemanticBranch(steps);
        AppendBudgetBranch(steps);
        AppendCorruptBranch(steps);

        var metrics = new Dictionary<string, object>
        {
            ["branches"] = string.Join(",", _store.ListBranches()),
            ["candidateCountBaseline"] = baselineCompute.Build!.Candidates.Count,
            ["status"] = "replayed"
        };
        bool allPassed = steps.All(s => s.Status is "accepted" or "SemanticallyUnsolvable"
            or "BudgetExhausted" or "FormatCorrupt" or "Success" or "Passed");
        return new ReplayReport(CasePackBuilder.ProjectId, CasePackBuilder.ProjectName,
            steps, allPassed, metrics);
    }

    private static string ComputeAffectedChain(EngineBuildResult build, string anchorId)
    {
        return build.Chains.FirstOrDefault(c => c.Anchors.Any(a => a.Id == anchorId))?.ChainId ?? "";
    }

    private static bool CheckLocality(EngineBuildResult before, EngineBuildResult after)
    {
        // Candidates for chains not containing the reservoir-corrected anchor must reproduce
        // identical band values; the chain containing a-06 must change.
        string changedChain = after.Chains.First(c => c.Anchors.Any(a => a.Id == "a-06")).ChainId;
        bool otherChainsStable = true;
        foreach (var chain in before.Chains.Where(c => c.ChainId != changedChain))
        {
            var b = before.CandidatesFor(chain.ChainId).OrderBy(c => c.Family).ToList();
            var a = after.CandidatesFor(chain.ChainId).OrderBy(c => c.Family).ToList();
            if (b.Count != a.Count) return false;
            for (int i = 0; i < b.Count; i++)
            {
                var bp = b[i].Band;
                var ap = a[i].Band;
                if (bp.Count != ap.Count) return false;
                for (int k = 0; k < bp.Count; k++)
                    if (Math.Abs(bp[k].Q500CalBp - ap[k].Q500CalBp) > 1e-9)
                        otherChainsStable = false;
            }
        }
        var changedBefore = before.CandidatesFor(changedChain).OrderBy(c => c.Family).ToList();
        var changedAfter = after.CandidatesFor(changedChain).OrderBy(c => c.Family).ToList();
        bool affectedChainChanged = false;
        for (int i = 0; i < changedBefore.Count; i++)
            for (int k = 0; k < changedBefore[i].Band.Count; k++)
                if (Math.Abs(changedBefore[i].Band[k].Q500CalBp - changedAfter[i].Band[k].Q500CalBp) > 1e-9)
                    affectedChainChanged = true;
        return otherChainsStable && affectedChainChanged;
    }
}
