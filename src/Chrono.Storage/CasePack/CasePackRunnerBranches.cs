using Chrono.Core.Engine;
using Chrono.Core.Events;
using Chrono.Core.Materials;
using Chrono.Core.Units;
using Chrono.Storage.Workflow;

namespace Chrono.Storage.CasePack;

public sealed record ConflictDescriptor(string BranchEventId, string MainDecisionId, string BranchDecisionId);

public partial class CasePackRunner
{
    private ConflictDescriptor AppendConflictingDecisions(List<ReplayStep> steps, string headEventId)
    {
        var mainDecision = new DecisionPayload(
            "dec-keep-aliquot-a", DecisionAction.ExcludeAnchor, "a-03b",
            "keep A3a: A3b is from the contaminated mixed aliquot half", null, null);
        var main = _workflow.RecordDecision(CasePackBuilder.ProjectId, "main",
            mainDecision, headEventId, "sedimentology-team");
        var mainCompute = _workflow.Compute(CasePackBuilder.ProjectId, "main",
            BuildBudget.Default, "sedimentology-team",
            "derivation after excluding duplicate aliquot A3b");

        var branchEventId = _workflow.Branch(CasePackBuilder.ProjectId,
            CasePackBuilder.CounterBranchId, headEventId,
            "counter-case: reviewer disputes which duplicate aliquot is trustworthy",
            "reviewer-b");
        var branchDecision1 = new DecisionPayload(
            "dec-keep-aliquot-b", DecisionAction.ExcludeAnchor, "a-03a",
            "counter opinion: A3a was mislabelled; keep A3b instead",
            "dec-keep-aliquot-a", null);
        var b1 = _workflow.RecordDecision(CasePackBuilder.ProjectId,
            CasePackBuilder.CounterBranchId, branchDecision1, branchEventId, "reviewer-b");
        var branchCompute = _workflow.Compute(CasePackBuilder.ProjectId,
            CasePackBuilder.CounterBranchId, BuildBudget.Default, "reviewer-b",
            "counter-case derivation keeping A3b");

        steps.Add(new ReplayStep("step-05-conflict-main", "human decision on main: exclude A3b", "main",
            new[] { "depth-duplicate" }, "accepted", main.Event!.Id,
            "duplicate group dup-185 at 1.85 m: aliquot A3b excluded",
            "derivation keeps A3a; decision recorded as event with rationale",
            new[] { "a-03b" }, new[] { headEventId, main.Event.Id, mainCompute.Event!.Id }));
        steps.Add(new ReplayStep("step-05-conflict-branch", "conflicting decision on counter branch: exclude A3a",
            CasePackBuilder.CounterBranchId, new[] { "depth-duplicate" }, "accepted", b1.Event!.Id,
            "same duplicate group, opposite aliquot kept; branch created from pre-decision head",
            "both decisions retained in separate branches; neither overwrites the other",
            new[] { "a-03a" },
            new[] { branchEventId, b1.Event.Id, branchCompute.Event!.Id }));

        return new ConflictDescriptor(branchEventId, "dec-keep-aliquot-a", "dec-keep-aliquot-b");
    }

    private ReplayStep AppendOptimisticConflict(ConflictDescriptor conford)
    {
        long seq = _store.NextSequence(CasePackBuilder.CounterBranchId);
        var events = _store.ReadEvents(CasePackBuilder.CounterBranchId);
        var head = events.Last();
        var e1 = ProjectEvent.Create(
            StableIds.Event(CasePackBuilder.ProjectId, "decision", $"opt-a:{seq}"),
            CasePackBuilder.ProjectId, CasePackBuilder.CounterBranchId, seq,
            new[] { head.Id }, EventKind.DecisionRecorded,
            new DecisionPayload("dec-opt-a", DecisionAction.PreferCandidate,
                "any", "optimistic writer A", null, null),
            "writer-a", _workflow.RuleSetVersion, DateTimeOffset.UtcNow, "optimistic-a");
        var e2 = ProjectEvent.Create(
            StableIds.Event(CasePackBuilder.ProjectId, "decision", $"opt-b:{seq}"),
            CasePackBuilder.ProjectId, CasePackBuilder.CounterBranchId, seq,
            new[] { head.Id }, EventKind.DecisionRecorded,
            new DecisionPayload("dec-opt-b", DecisionAction.PreferCandidate,
                "any", "optimistic writer B", null, null),
            "writer-b", _workflow.RuleSetVersion, DateTimeOffset.UtcNow, "optimistic-b");
        var r1 = _store.Append(e1, seq, Array.Empty<string>());
        var r2 = _store.Append(e2, seq, Array.Empty<string>());
        bool exactlyOne = (r1.Ok && r2.Failure == AppendFailure.ConcurrentWrite)
                          || (r2.Ok && r1.Failure == AppendFailure.ConcurrentWrite);
        return new ReplayStep("step-07-optimistic-concurrency",
            "two appends at the same sequence: exactly one wins",
            CasePackBuilder.CounterBranchId, new[] { "concurrent-freeze" },
            exactlyOne ? "accepted" : "FAILED",
            r1.Ok ? e1.Id : e2.Id,
            $"both writers expected sequence {seq} on branch {CasePackBuilder.CounterBranchId}",
            exactlyOne
                ? "one append committed, the other received ConcurrentWrite and must re-read"
                : "optimistic concurrency did not isolate the writers",
            new[] { r1.Ok ? e1.Id : e2.Id },
            new[] { head.Id });
    }

    private void AppendSemanticBranch(List<ReplayStep> steps)
    {
        var branch = "inversion-check";
        var importHead = _store.ReadEvents("main").First();
        var branchEvent = _workflow.Branch(CasePackBuilder.ProjectId, branch,
            importHead.Id, "counter: unflagged inversion inside an unbroken segment", "reviewer-b");
        var project = CasePackBuilder.Baseline() with
        {
            Boundaries = CasePackBuilder.Baseline().Boundaries
                .Where(b => b.Kind != BoundaryKind.InversionMarker).ToList()
        };
        var commit = _workflow.CommitMaterials(CasePackBuilder.ProjectId, branch,
            project, 1, importHead.Id, null, "reviewer-b",
            new[] { "bdy-inv-365-removed" },
            "counter source: inversion marker removed, ages 12100 > 11800 in unbroken segment B");
        var compute = _workflow.Compute(CasePackBuilder.ProjectId, branch,
            BuildBudget.Default, "reviewer-b", "expected SEMANTIC rejection");
        var pass = compute.Build!.Status == BuildStatus.SemanticallyUnsolvable
                   && compute.Build.Report.Diagnostics
                       .Any(d => d.Code == "SEMANTIC-INVERTED-AGE");
        steps.Add(new ReplayStep("step-08-semantic-inversion",
            "unflagged inverted ages in unbroken segment -> semantic no-solution",
            branch, new[] { "inverted-segment" },
            pass ? "SemanticallyUnsolvable" : "FAILED", commit.Event!.Id,
            "A7 12100 cal BP at 3.60 m sits above A8 11800 cal BP at 3.70 m; no unconformity between them",
            pass
                ? "engine refuses every monotone model; structured diagnosis with source position returned"
                : "inversion was not rejected",
            new[] { "chain-segment-B" },
            new[] { branchEvent, commit.Event.Id, compute.Event!.Id }));
    }

    private void AppendBudgetBranch(List<ReplayStep> steps)
    {
        var branch = "budget-check";
        var importHead = _store.ReadEvents("main").First();
        var branchEvent = _workflow.Branch(CasePackBuilder.ProjectId, branch,
            importHead.Id, "counter: resource budget exhausted during derivation", "reviewer-b");
        var commit = _workflow.CommitMaterials(CasePackBuilder.ProjectId, branch,
            CasePackBuilder.Baseline(), 1, importHead.Id, null, "reviewer-b",
            new[] { "all-materials" }, "counter: baseline under a deliberately tiny budget");
        var compute = _workflow.Compute(CasePackBuilder.ProjectId, branch,
            BuildBudget.Tiny, "reviewer-b", "expected BUDGET exhaustion");
        var pass = compute.Build!.Status == BuildStatus.BudgetExhausted
                   && compute.Build.Report.Diagnostics
                       .Any(d => d.Code == "BUDGET-EXHAUSTED");
        steps.Add(new ReplayStep("step-09-budget-exhausted",
            "budget runs out before all candidates evaluated -> distinct budget result",
            branch, Array.Empty<string>(),
            pass ? "BudgetExhausted" : "FAILED", commit.Event!.Id,
            "budget: at most 2 candidates, 8 samples, 1 ms",
            pass
                ? "partial candidates kept with BudgetExhausted status, distinct from semantic/format failures"
                : "budget limit not signalled",
            compute.Build.Candidates.Select(c => c.CandidateId).ToList(),
            new[] { branchEvent, commit.Event.Id, compute.Event!.Id }));
    }

    private void AppendCorruptBranch(List<ReplayStep> steps)
    {
        var branch = "corrupt-check";
        var importHead = _store.ReadEvents("main").First();
        var branchEvent = _workflow.Branch(CasePackBuilder.ProjectId, branch,
            importHead.Id, "counter: malformed source fragment", "reviewer-b");
        var baseline = CasePackBuilder.Baseline();
        var corrupt = baseline with
        {
            RawTokens = baseline.RawTokens.Append(
                new RawToken("external-table-7", "depth", "n/a m",
                    RawParseStatus.Malformed, null, "cannot parse numeric part 'n/a'")).ToList()
        };
        var commit = _workflow.CommitMaterials(CasePackBuilder.ProjectId, branch,
            corrupt, 1, importHead.Id, "external-table-7", "reviewer-b",
            new[] { "external-table-7" },
            "counter: appended source row carries an unparseable depth");
        var compute = _workflow.Compute(CasePackBuilder.ProjectId, branch,
            BuildBudget.Default, "reviewer-b", "expected FORMAT rejection");
        var pass = compute.Build!.Status == BuildStatus.FormatCorrupt
                   && compute.Build.Report.Diagnostics
                       .Any(d => d.Code == "FORMAT-CORRUPT" && d.SourceId == "external-table-7");
        steps.Add(new ReplayStep("step-10-format-corrupt",
            "malformed input -> distinct format-corrupt result, no derivation faked",
            branch, Array.Empty<string>(),
            pass ? "FormatCorrupt" : "FAILED", commit.Event!.Id,
            "external-table-7 row depth = 'n/a m'",
            pass
                ? "engine returns positioned FORMAT-CORRUPT diagnostic and performs no computation"
                : "malformed input was not rejected",
            new[] { "external-table-7" },
            new[] { branchEvent, commit.Event.Id, compute.Event!.Id }));
    }
}
