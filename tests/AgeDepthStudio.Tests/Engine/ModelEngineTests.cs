using AgeDepthStudio.Domain;
using AgeDepthStudio.Engine;
using Xunit;

namespace AgeDepthStudio.Tests.Engine;

public class ModelEngineTests
{
    private readonly ModelEngine _engine = new();

    private StudioService Setup(StudioService svc, params (string id, string depth, double[] samples)[] anchors)
    {
        TestSupport.MiniProject(svc);
        foreach (var (id, depth, smp) in anchors)
            svc.AddAnchor("T", new AgeAnchor(id, "S1", depth, "L-" + id, "foram", smp, null));
        return svc;
    }

    private ModelResult Run(StudioService svc)
    {
        var s = svc.State("T");
        return _engine.Compute(s, s.ActiveParams, "Mx", "").Result;
    }

    [Fact]
    public void Monotonic_within_segment_and_explicit_boundary_between_segments()
    {
        var (svc, _) = TestSupport.New();
        Setup(svc,
            ("A1", "1m", TestSupport.Samples(10, .3, seed: 1)),
            ("A2", "5m", TestSupport.Samples(20, .3, seed: 2)),
            ("A3", "13m", TestSupport.Samples(5, .3, seed: 3))); // younger across unconformity is legal
        var m = Run(svc);
        Assert.Equal(OutcomeKind.Ok, m.OverallOutcome);
        var s1 = m.Segments.Single(r => r.SegmentId == "S1");
        Assert.All(s1.Candidates[0].Knots, _ => { });
        Assert.Contains(m.Segments, r => r.SegmentId == "S2");
        // 段内 q50 非降
        var knots = s1.Candidates[0].Knots;
        for (var i = 1; i < knots.Length; i++) Assert.True(knots[i].Q50 >= knots[i - 1].Q50 - 1e-9);
    }

    [Fact]
    public void Tied_anchors_produce_all_equal_optimum_candidates()
    {
        var (svc, _) = TestSupport.New();
        Setup(svc,
            ("A1", "1m", TestSupport.Samples(10, .3, seed: 1)),
            ("A2", "5m", TestSupport.Samples(30, .4, seed: 2)),
            ("A3", "5m", TestSupport.Samples(30, .4, seed: 3)));
        var m = Run(svc);
        var seg = m.Segments.Single(r => r.SegmentId == "S1");
        Assert.Equal(2, seg.Candidates.Count);
        Assert.Equal(2, seg.Candidates.Select(c => c.CandidateId).Distinct().Count());
    }

    [Fact]
    public void Strict_inversion_is_semantic_unsolvable_not_budget_or_format()
    {
        var (svc, _) = TestSupport.New();
        // shallow old-ish (q99 small), deep definitively younger (q01 large younger number)
        Setup(svc,
            ("A1", "1m", TestSupport.Samples(30, .2, seed: 1)),
            ("A2", "8m", TestSupport.Samples(8, .2, seed: 2)));
        var m = Run(svc);
        Assert.Equal(OutcomeKind.SemanticUnsolvable, m.Segments.Single(r => r.SegmentId == "S1").Outcome);
        Assert.Equal(OutcomeKind.SemanticUnsolvable, m.OverallOutcome);
    }

    [Fact]
    public void Budget_exhaustion_is_distinct_outcome()
    {
        var (svc, _) = TestSupport.New();
        TestSupport.MiniProject(svc);
        svc.IssueParams("T", new RuleParams("P2", 2, 1, 1.0, 2.5, "预算1"), true);
        // two tie groups of 2 -> 4 combos > 1
        svc.AddAnchor("T", new AgeAnchor("A1", "S1", "1m", "L", "f", TestSupport.Samples(10, .2, seed: 1), null));
        svc.AddAnchor("T", new AgeAnchor("A2", "S1", "1m", "L", "f", TestSupport.Samples(10, .2, seed: 2), null));
        svc.AddAnchor("T", new AgeAnchor("A3", "S1", "5m", "L", "f", TestSupport.Samples(20, .2, seed: 3), null));
        svc.AddAnchor("T", new AgeAnchor("A4", "S1", "5m", "L", "f", TestSupport.Samples(20, .2, seed: 4), null));
        var m = Run(svc);
        Assert.Equal(OutcomeKind.BudgetExhausted, m.Segments.Single(r => r.SegmentId == "S1").Outcome);
    }

    [Fact]
    public void Unconvertible_depth_is_format_corrupt_with_diagnostic_and_value_kept()
    {
        var (svc, _) = TestSupport.New();
        Setup(svc, ("A1", "深度?", TestSupport.Samples(10, .2, seed: 1)));
        var m = Run(svc);
        var seg = m.Segments.Single(r => r.SegmentId == "S1");
        Assert.Equal(OutcomeKind.FormatCorrupt, seg.Outcome);
        // 原值仍在材料中
        Assert.Equal("深度?", svc.State("T").Anchors.Single().DepthRaw);
        Assert.Contains(seg.Diagnostics, d => d.Code == "ANCHOR_DEPTH_UNCONVERTIBLE" && d.JsonPointer == "/depthRaw");
    }

    [Fact]
    public void Long_tail_distribution_is_retained_not_replaced_by_mean()
    {
        var (svc, _) = TestSupport.New();
        var tail = TestSupport.Skew(20, .4, 40, 200, 9);
        Setup(svc, ("A1", "1m", TestSupport.Samples(10, .3, seed: 1)),
                    ("A2", "8m", tail));
        var outp = _engine.Compute(svc.State("T"), svc.State("T").ActiveParams, "Mx", "");
        Assert.Contains(outp.Judgments, j => j.Type == "judgment.long_tail");
        var knot = outp.Result.Segments.Single(r => r.SegmentId == "S1").Candidates[0].Knots[^1];
        // 长尾把 q99 推高，q99-q50 明显大于 q50-q01
        Assert.True(knot.Q99 - knot.Q50 > knot.Q50 - knot.Q01);
        Assert.True(knot.Q99 > tail.Average() + 5);
    }

    [Fact]
    public void Reservoir_offset_correction_derived_only()
    {
        var (svc, _) = TestSupport.New();
        Setup(svc, ("A1", "5m", TestSupport.Samples(20, .3, seed: 1)));
        var rawMean = svc.State("T").Anchors.Single().Samples.Average();
        svc.AddOffset("T", new ReservoirOffset("O1", "A1", 1.5, .3, "老碳"));
        var m = Run(svc);
        var q50 = m.Segments.Single(r => r.SegmentId == "S1").Candidates[0].Knots[0].Q50;
        Assert.True(q50 < rawMean - 1.0);
        Assert.Equal(rawMean, svc.State("T").Anchors.Single().Samples.Average(), 6); // 原始不动
    }

    [Fact]
    public void Empty_samples_is_rejected_not_defaulted()
    {
        var (svc, _) = TestSupport.New();
        Setup(svc, ("A1", "1m", TestSupport.Samples(10, .3, seed: 1)),
                    ("A2", "5m", Array.Empty<double>()));
        var m = Run(svc);
        var seg = m.Segments.Single(r => r.SegmentId == "S1");
        Assert.Contains(seg.Diagnostics, d => d.Code == "ANCHOR_NO_SAMPLES");
    }

    [Fact]
    public void Equivalent_inputs_have_stable_candidate_order()
    {
        ModelResult Build(int seedA, int seedB)
        {
            var (svc, _) = TestSupport.New();
            Setup(svc,
                ("A1", "1m", TestSupport.Samples(10, .3, seed: 11)),
                ("A2", "5m", TestSupport.Samples(20, .3, seed: seedA)),
                ("A3", "5m", TestSupport.Samples(20, .3, seed: seedB)));
            return Run(svc);
        }
        var m1 = Build(2, 3);
        var m2 = Build(2, 3);
        var ids1 = m1.Segments.Single().Candidates.Select(c => c.CandidateId).ToList();
        var ids2 = m2.Segments.Single().Candidates.Select(c => c.CandidateId).ToList();
        Assert.Equal(ids1, ids2);
    }
}
