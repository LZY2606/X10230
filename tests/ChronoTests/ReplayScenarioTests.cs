using System.Linq;
using ChronoCore;
using Xunit;

namespace ChronoTests;

public class ReplayScenarioTests
{
    [Theory]
    [InlineData("depth-duplicate", "semantic-impossible")]
    [InlineData("unconformity", "success-contained")]
    [InlineData("inverted-section", "semantic-impossible")]
    [InlineData("tie-anchor", "success-contained")]
    [InlineData("long-tail", "success-contained")]
    [InlineData("local-recompute", "success-contained")]
    [InlineData("concurrent-freeze", "success-contained")]
    public void EachScenario_ProducesExpectedOutcome_AndStructuredJudgment(string scenario, string expected)
    {
        var (bundle, state, p1, app, sink) = TestHarness.Baseline("v2");
        var p2 = state.Parameters["v2"];
        var digest = app.Engine.ComputeInputDigest(state, p2);
        var runner = new ReplayRunner(app);
        var sc = bundle.ReplayScenarios.Single(s => s.Scenario == scenario);

        // 在独立分支上跑（每个分支隔离，冻结锁不相互干扰）
        var branch = "counter-" + scenario;
        app.ForkBranch(branch, "main", sink.Events.Count, digest, scenario, sc.Title);
        var branchState = EventFolder.FoldTo(sink.Events, branch, null);
        var result = runner.Run(sc, branchState, p2, branch, sink.Events.Count, digest);

        Assert.Equal(expected, result.Outcome);
        var judgment = sink.Events.Where(e => e.BranchId == branch && e.Type == EventType.ReplayJudgment)
            .Select(e => System.Text.Json.JsonSerializer.Deserialize<JudgmentPayload>(
                e.Payload, new System.Text.Json.JsonSerializerOptions { PropertyNameCaseInsensitive = true }))
            .Single();
        Assert.Equal(sc.PropositionCode, judgment.PropositionCode);
        Assert.False(string.IsNullOrEmpty(judgment.AffectedScopeJson));
        Assert.Contains(scenario, judgment.InputFragmentIds);
        Assert.Equal(sc.InputFragmentJson, judgment.InputFragmentJson);
    }

    [Fact]
    public void ConcurrentFreeze_SecondHolderRejected_BothEventsKept()
    {
        var (bundle, state, p2, app, sink) = TestHarness.Baseline("v2");
        var param = state.Parameters["v2"];
        var engine = new DerivationEngine();
        var model = engine.Derive(state, param).Candidates.First(m => m.SemanticallyValid);

        var first = app.TryFreeze("main", model.Id, model.InputDigest, "v2", "reviewer-A",
            out var c1, out var h1);
        var second = app.TryFreeze("main", model.Id, model.InputDigest, "v2", "reviewer-B",
            out var c2, out var h2);

        Assert.True(first);
        Assert.False(second);
        Assert.Equal("reviewer-A", h2);
        Assert.NotNull(c2);
        Assert.Equal(EventType.FreezeConflict, c2!.Type);
        Assert.False(c2.Adopted);
        Assert.Contains(sink.Events, e => e.Type == EventType.ModelFrozen && e.Adopted);
        Assert.Contains(sink.Events, e => e.Type == EventType.FreezeConflict && !e.Adopted);
    }

    [Fact]
    public void ImpactComparison_AfterFreeze_DoesNotMutateFrozenModel()
    {
        var (bundle, state, p2, app, sink) = TestHarness.Baseline("v2");
        var param = state.Parameters["v2"];
        var engine = new DerivationEngine();
        var frozen = engine.Derive(state, param).Candidates.First(m => m.SemanticallyValid);
        var trial = new Anchor("an-trial-1", "PL-X", "26.0 mbsf", "dat-mbsf",
            SamplesFactory.Normal(160000, 4000), "AMS14C", 950, "res-local");
        var report = engine.CompareNewAnchor(state, frozen, trial, null, param);

        Assert.True(report.MonotonicPreserved);
        // 新锚点 26 m 位于 iv-2，影响必须限制在 iv-2，不跨越 unc-1/gap-1
        Assert.Equal(new[] { "iv-2" }, report.AffectedIntervalIds);
        Assert.NotNull(report.MaxAgeShiftCalA);
    }
}
