using ChronoCore;
using Xunit;

namespace ChronoTests;

public class DepthAgeModelTests
{
    [Fact]
    public void Baseline_DerivesMultipleValidCandidates_AndKeepsTie()
    {
        var (bundle, state, p1, _, _) = TestHarness.Baseline();
        var outcome = new DerivationEngine().Derive(state, p1);
        Assert.Equal(OutcomeKind.Success, outcome.Kind);
        Assert.True(outcome.Candidates.Count >= 2, "并列锚点应产生多套候选");
        Assert.Contains(outcome.Candidates, m => m.Tie && m.SemanticallyValid);
        // 三个连续段都应有速率/包络，且在两个显式边界处断开
        foreach (var iv in new[] { "iv-1", "iv-2", "iv-3" })
        {
            Assert.Contains(outcome.Candidates.SelectMany(m => m.Rates), r => r.IntervalId == iv);
            Assert.Contains(outcome.Candidates.SelectMany(m => m.Envelope), e => e.IntervalId == iv);
        }
        // 等优输入稳定顺序：跑两次 rank 完全一致
        var again = new DerivationEngine().Derive(state, p1);
        Assert.Equal(
            outcome.Candidates.Select(m => m.Id).ToList(),
            again.Candidates.Select(m => m.Id).ToList());
    }

    [Fact]
    public void MonotonicOnlyWithinContinuousSection_AcrossUnconformityAllowed()
    {
        var (bundle, state, p1, _, _) = TestHarness.Baseline();
        // 不整合之下 an-06(148ka) 浅/老于上覆 an-05(38.9ka) —— 跨 unc-1，合法
        var outcome = new DerivationEngine().Derive(state, p1);
        Assert.Equal(OutcomeKind.Success, outcome.Kind);
        Assert.DoesNotContain(outcome.Diagnostics, d => d.Code == "D-NONMONOTONIC");

        // 同一连续段 iv-1 内放入下浅上老的锚点 → 语义无解
        var state2 = Clone(state);
        state2.Anchors["an-inv"] = new Anchor("an-inv", "PL-INV", "16.80 mbsf", "dat-mbsf",
            SamplesFactory.Normal(20000, 2000), "AMS14C", 97, "res-local");
        state2.AnchorOrder.Add(state2.Anchors["an-inv"]);
        var fail = new DerivationEngine().Derive(state2, p1);
        Assert.Equal(OutcomeKind.SemanticallyImpossible, fail.Kind);
        Assert.Contains(fail.Diagnostics, d => d.Code == "D-NONMONOTONIC");
    }

    [Fact]
    public void DepthDuplicate_Undeclared_IsSemanticImpossible_DeclaredTie_KeepsBothCandidates()
    {
        var (_, state, p1, _, _) = TestHarness.Baseline();
        var s2 = Clone(state);
        s2.Anchors["an-dup"] = new Anchor("an-dup", "DUP", "15.20 mbsf", "dat-mbsf",
            SamplesFactory.Normal(330000, 3000), "AMS14C", 99, "res-local");
        s2.AnchorOrder.Add(s2.Anchors["an-dup"]);
        var fail = new DerivationEngine().Derive(s2, p1);
        Assert.Equal(OutcomeKind.SemanticallyImpossible, fail.Kind);
        Assert.Contains(fail.Diagnostics, d => d.Code == "D-DEPTH-DUPLICATE");

        // 基线内置同深度并列组 grp-tie-12：不是错误，候选数翻倍且等优保留
        var ok = new DerivationEngine().Derive(state, p1);
        var pickSets = ok.Candidates.SelectMany(m => m.ChosenAnchorIds)
            .Count(id => id is "an-tie-a" or "an-tie-b");
        Assert.True(pickSets >= 2);
    }

    [Fact]
    public void DistributionIsNeverReplacedByMean_LongTailWidens99Band()
    {
        var (_, state, _, _, _) = TestHarness.Baseline();
        var a4 = state.Anchors["an-04"];
        Assert.True(a4.Samples.LongTail);
        var w95 = a4.Samples.Q975 - a4.Samples.Q025;
        var w99 = a4.Samples.Q995 - a4.Samples.Q005;
        Assert.True(w99 > w95 * 1.2, "学生-t 长尾应让 99% 带明显更宽");

        var p1 = state.Parameters["v1"];
        var model = new DerivationEngine().Derive(state, p1).Candidates[0];
        var env04 = model.Envelope.Where(e => e.IntervalId == "iv-1").ToList();
        Assert.All(env04, e => Assert.True(e.High99 - e.Low99 >= e.High95 - e.Low95 - 1e-6));
    }

    [Fact]
    public void BudgetExhausted_IsDistinctFromSemanticImpossible_AndFormatCorrupted()
    {
        var (_, state, _, _, _) = TestHarness.Baseline();
        var tight = state.Parameters["v1"] with { MaxCandidates = 1 };
        var outcome = new DerivationEngine().Derive(state, tight);
        Assert.Equal(OutcomeKind.BudgetExhausted, outcome.Kind);
        Assert.Contains(outcome.Diagnostics, d => d.Code == "D-BUDGET");

        // 倒置层段是语义无解，不是预算问题
        var s2 = Clone(state);
        s2.Anchors["an-inv2"] = new Anchor("an-inv2", "INV", "16.9 mbsf", "dat-mbsf",
            SamplesFactory.Normal(20000, 1000), "AMS14C", 96, "res-local");
        s2.AnchorOrder.Add(s2.Anchors["an-inv2"]);
        var semantic = new DerivationEngine().Derive(s2, tight with { MaxCandidates = 100 });
        Assert.Equal(OutcomeKind.SemanticallyImpossible, semantic.Kind);

        // 坏插值参数属于格式/配置损坏
        var badParam = tight with { Interpolation = "spline-xyz", MaxCandidates = 100 };
        var corrupt = new DerivationEngine().Derive(state, badParam);
        Assert.Equal(OutcomeKind.FormatCorrupted, corrupt.Kind);
    }

    [Fact]
    public void LocalRecompute_OnlyTouchesDependentInterval_ContentAddressed()
    {
        var (_, state, _, _, _) = TestHarness.Baseline("v2");
        var engine = new DerivationEngine();
        var p2 = state.Parameters["v2"];
        var before = engine.Derive(state, p2);

        var s2 = Clone(state);
        var revised = s2.Anchors["an-07"] with
        {
            Samples = SamplesFactory.StudentT(172500, 4800, 7),
            Note = "局部重算"
        };
        s2.Anchors["an-07"] = revised;
        s2.AnchorOrder[s2.AnchorOrder.FindIndex(a => a.Id == "an-07")] = revised;
        var after = engine.Derive(s2, p2, new[] { "iv-2" });

        var mBefore = before.Candidates.First(m => m.SemanticallyValid);
        var mAfter = after.Candidates.First(m => m.SemanticallyValid);
        // iv-1 / iv-3 包络完全一致，iv-2 发生变化
        foreach (var iv in new[] { "iv-1", "iv-3" })
            Assert.Equal(
                mBefore.Envelope.Where(e => e.IntervalId == iv).Select(e => e.MedianAge).ToList(),
                mAfter.Envelope.Where(e => e.IntervalId == iv).Select(e => e.MedianAge).ToList());
        Assert.NotEqual(
            mBefore.Envelope.Where(e => e.IntervalId == "iv-2").Select(e => e.MedianAge).ToList(),
            mAfter.Envelope.Where(e => e.IntervalId == "iv-2").Select(e => e.MedianAge).ToList());
    }

    [Fact]
    public void UnconvertibleValues_StayInPlaceWithDiagnostics_NoFakeDefaults()
    {
        var (_, state, _, _, _) = TestHarness.Baseline();
        var q = Quantity.Depth("29.6 ???", "dat-mbsf");
        Assert.False(q.IsConvertible);
        Assert.Equal("Q-UNKNOWN-UNIT", q.DiagnosticCode);
        Assert.Equal("29.6 ???", q.Raw);

        var raw14c = Quantity.Age("30000 14C a BP");
        Assert.False(raw14c.IsConvertible);
        Assert.Equal("Q-RADIOCARBON-RAW", raw14c.DiagnosticCode);

        var p1 = state.Parameters["v1"];
        var outcome = new DerivationEngine().Derive(state, p1);
        // an-bad 保留并被排除，绝不出现在任何候选的锚点选择中
        Assert.DoesNotContain(outcome.Candidates.SelectMany(m => m.ChosenAnchorIds), id => id == "an-bad");
        Assert.Contains(outcome.Diagnostics, d => d.MaterialId == "an-bad");
    }

    internal static ProjectState Clone(ProjectState s) => ReplayRunner.CloneState(s);
}
