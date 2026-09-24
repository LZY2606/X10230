using AgeDepthStudio.Domain;
using AgeDepthStudio.Engine;
using Xunit;

namespace AgeDepthStudio.Tests.Workflow;

public class WorkflowTests
{
    private static void Seed(StudioService svc, string ws = "T")
    {
        TestSupport.MiniProject(svc, ws);
        svc.AddAnchor(ws, new AgeAnchor("A1", "S1", "1m", "L1", "f", TestSupport.Samples(10, .3, seed: 1)));
        svc.AddAnchor(ws, new AgeAnchor("A2", "S1", "8m", "L2", "f", TestSupport.Samples(18, .3, seed: 2)));
        svc.AddAnchor(ws, new AgeAnchor("A3", "S2", "13m", "L3", "f", TestSupport.Samples(30, .3, seed: 3)));
        svc.AddAnchor(ws, new AgeAnchor("A4", "S2", "18m", "L4", "f", TestSupport.Samples(38, .3, seed: 4)));
        svc.AddLayer(ws, new EventLayer("E1", ws, "S1", "5m", "tephra", null, "火山灰"));
    }

    [Fact]
    public void Local_recompute_changes_only_dependent_segment()
    {
        var (svc, store) = TestSupport.New();
        Seed(svc);
        svc.Recompute("T", "M1");
        var s0 = svc.State("T");
        var p = s0.ActiveParams;
        var fp1Before = ModelEngine.SegmentFingerprint(s0, p, "S1");
        var fp2Before = ModelEngine.SegmentFingerprint(s0, p, "S2");
        var candS2Before = s0.Models.Last().Segments.Single(r => r.SegmentId == "S2").Candidates
            .Select(c => c.CandidateId).ToList();

        var rev = s0.Anchors.Single(a => a.AnchorId == "A3") with { Samples = TestSupport.Samples(30.8, .28, seed: 99) };
        svc.ReviseAnchor("T", rev, "局部校准");

        var s1 = svc.State("T");
        var p1 = s1.ActiveParams;
        Assert.Equal(fp1Before, ModelEngine.SegmentFingerprint(s1, p1, "S1"));
        Assert.NotEqual(fp2Before, ModelEngine.SegmentFingerprint(s1, p1, "S2"));
        var last = s1.Models.Last();
        var candS2After = last.Segments.Single(r => r.SegmentId == "S2").Candidates.Select(c => c.CandidateId).ToList();
        Assert.NotEqual(candS2Before, candS2After);
    }

    [Fact]
    public void Conflicting_decisions_are_both_retained_and_audited()
    {
        var (svc, store) = TestSupport.New();
        Seed(svc);
        svc.RecordDecision("T", new Decision("D1", "A2", DecisionAction.ExcludeAnchor, "疑再搬运", "li", "2026-09-21T01:00:00Z"), "li");
        svc.RecordDecision("T", new Decision("D2", "A2", DecisionAction.ReinstateAnchor, "原位", "wang", "2026-09-21T02:00:00Z"), "wang");
        var s = svc.State("T");
        Assert.Equal(2, s.Decisions.Count(d => d.TargetId == "A2"));
        Assert.Contains(store.Events("T"), e => e.Type == "decision.conflict");
        // 最新决定生效但历史仍在
        svc.Recompute("T", "M1");
        var seg = svc.State("T").Models.Last().Segments.Single(r => r.SegmentId == "S1");
        Assert.DoesNotContain(seg.Diagnostics, d => d.Code == "ANCHOR_EXCLUDED_BY_DECISION");
    }

    [Fact]
    public void Freeze_is_optimistic_concurrency_controlled()
    {
        var (svc, store) = TestSupport.New();
        Seed(svc);
        svc.Recompute("T", "M1");
        // 参数前进一版
        svc.IssueParams("T", new RuleParams("P2", 2, 1000, 1.05, 2.5, "新参数"), true);
        var okStale = svc.TryFreeze("T", "M1", "P1", "reviewer-a", out var conflict);
        Assert.False(okStale);
        Assert.NotNull(conflict);
        Assert.Equal("freeze.conflict", conflict!.Type);
        var okFresh = svc.TryFreeze("T", "M1", "P2", "reviewer-b", out var conflict2);
        Assert.True(okFresh);
        Assert.Null(conflict2);
        var okAgain = svc.TryFreeze("T", "M1", "P2", "reviewer-c", out var conflict3);
        Assert.False(okAgain);
        Assert.Equal("freeze.conflict", conflict3!.Type);
    }

    [Fact]
    public void Trial_after_freeze_is_non_destructive()
    {
        var (svc, store) = TestSupport.New();
        Seed(svc);
        svc.Recompute("T", "M1");
        svc.TryFreeze("T", "M1", "P1", "lead", out _);
        var anchorsBefore = svc.State("T").Anchors.Count;
        var t = svc.RunTrial("T", new TrialRequest("T-X", "A9(new)", "冻结后新锚点",
            new TrialOverride("anchor-add", "A9", "6m", TestSupport.Samples(15, .3, seed: 44), "S1")), false);
        Assert.Contains("S1", t.AffectedSegmentIds);
        Assert.False(t.Adopted);
        Assert.Equal(anchorsBefore, svc.State("T").Anchors.Count);
        Assert.Contains(store.Events("T"), e => e.Type == "trial.result");
        Assert.Contains(svc.State("T").Trials, x => x.TrialId == "T-X" && !x.Adopted);
    }

    [Fact]
    public void Time_travel_restores_old_dependencies_and_conclusions()
    {
        var (svc, store) = TestSupport.New();
        Seed(svc);
        svc.Recompute("T", "M1");
        var atM1 = store.Events("T").Count;
        svc.IssueParams("T", new RuleParams("P2", 2, 1000, 1.1, 2.5, "新"), true);
        svc.Recompute("T", "M2");
        var latest = svc.State("T");
        Assert.Equal("P2", latest.ActiveParams.ParamsId);
        var old = svc.State("T", atM1);
        Assert.Equal("P1", old.ActiveParams.ParamsId);
        Assert.Single(old.Models);
        Assert.Equal(2, latest.Models.Count);
    }

    [Fact]
    public void Trial_impact_scope_is_local_for_single_source_change()
    {
        var (svc, _) = TestSupport.New();
        Seed(svc);
        svc.Recompute("T", "M1");
        var t = svc.RunTrial("T", new TrialRequest("T-L", "A1", "单锚点微调",
            new TrialOverride("anchor", "A1", null, TestSupport.Samples(10.4, .3, seed: 7))), false);
        Assert.Equal("Local", t.ImpactScope);
        Assert.Equal(new[] { "S1" }, t.AffectedSegmentIds);
    }

    [Fact]
    public void Unconformity_gap_change_is_boundary_scoped()
    {
        var (svc, _) = TestSupport.New();
        Seed(svc);
        svc.Recompute("T", "M1");
        var t = svc.RunTrial("T", new TrialRequest("T-U", "G1", "改判不整合",
            new TrialOverride("gap", "G1")), false);
        Assert.Equal("Local", t.ImpactScope);
        Assert.Contains("S1", t.AffectedSegmentIds);
        Assert.Contains("S2", t.AffectedSegmentIds);
    }

    [Fact]
    public void Params_budget_change_is_workspace_wide()
    {
        var (svc, _) = TestSupport.New();
        Seed(svc);
        svc.Recompute("T", "M1");
        var t = svc.RunTrial("T", new TrialRequest("T-B", "P-budget", "预算变更",
            new TrialOverride("params", "P1", null, null, null, 1)), false);
        Assert.Equal("WorkspaceWide", t.ImpactScope);
        Assert.Equal(OutcomeKind.BudgetExhausted, t.Outcome);
    }

    [Fact]
    public void Every_model_result_records_inputs_params_and_parent()
    {
        var (svc, store) = TestSupport.New();
        Seed(svc);
        svc.Recompute("T", "M1");
        svc.Recompute("T", "M2");
        var payloads = store.Events("T").Where(e => e.Type == "model.recomputed").ToList();
        Assert.Equal(2, payloads.Count);
        using var doc0 = System.Text.Json.JsonDocument.Parse(payloads[0].PayloadJson);
        using var doc1 = System.Text.Json.JsonDocument.Parse(payloads[1].PayloadJson);
        Assert.Equal("M1", doc0.RootElement.GetProperty("result").GetProperty("modelId").GetString());
        Assert.Equal("P1", doc0.RootElement.GetProperty("paramsId").GetString());
        Assert.Equal("M1", doc1.RootElement.GetProperty("parentModelId").GetString());
        Assert.True(doc0.RootElement.GetProperty("inputSummary").GetProperty("anchorIds").GetArrayLength() == 4);
    }
}
