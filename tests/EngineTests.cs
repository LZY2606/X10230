using DepthAge.Core;
using Xunit;

namespace DepthAge.Tests;

public class EngineTests
{
    private static SegmentInput Seg(string r, double top, double bottom, string tb = "continuous", string bb = "continuous") =>
        new(r, new Quantity(top, "m", "mbsf"), new Quantity(bottom, "m", "mbsf"), tb, bb);

    private static AnchorInput Anc(string r, double depthM, AgeDistribution age, string depthUnit = "m") =>
        new(r, $"age-anchor:{r}", "age-anchor", new Quantity(depthM, depthUnit, "mbsf"), age, new Quantity(0, "ka"));

    private static AgeDistribution N(double mean, double sigma) =>
        new(AgeDistribution.Normal, "ka", mean, sigma, null, null, null);

    [Fact]
    public void Monotonic_WithinUnbrokenSegment_Enforced()
    {
        var fit = Engine.Fit(
            new[] { Seg("S1", 0, 3) },
            new[] { Anc("A", 0.5, N(1, 0.05)), Anc("B", 1.5, N(2, 0.05)), Anc("C", 2.5, N(3, 0.05)) },
            new EngineParams());
        Assert.Equal(FitStatus.Ok, fit.Status);
        var seg = Assert.Single(fit.Model!.Segments);
        Assert.NotEmpty(seg.Candidates);
        foreach (var c in seg.Candidates)
            for (var i = 1; i < c.AgesKa.Length; i++)
                Assert.True(c.AgesKa[i] >= c.AgesKa[i - 1] - 1e-9, "candidate ages must be non-decreasing with depth");
    }

    [Fact]
    public void AgeJump_AcrossUnconformity_Allowed()
    {
        var fit = Engine.Fit(
            new[] { Seg("S1", 0, 1, "surface", "unconformity"), Seg("S2", 1, 2, "unconformity", "core-bottom") },
            new[]
            {
                Anc("A", 0.2, N(2.0, 0.05)), Anc("B", 0.8, N(3.0, 0.05)),
                Anc("C", 1.2, N(0.5, 0.05)), Anc("D", 1.8, N(0.9, 0.05))
            },
            new EngineParams());
        Assert.Equal(FitStatus.Ok, fit.Status);
        Assert.DoesNotContain(fit.Diagnostics, d => d.Code == "inverted-segment");
        Assert.Contains(fit.Diagnostics, d => d.Code == "unconformity-boundary");
    }

    [Fact]
    public void InvertedSegment_IsSemanticFailure_DistinctResult()
    {
        var fit = Engine.Fit(
            new[] { Seg("S1", 0, 2) },
            new[] { Anc("A", 0.5, N(5.0, 0.05)), Anc("B", 1.5, N(1.0, 0.05)) },
            new EngineParams());
        Assert.Equal(FitStatus.Unsatisfiable, fit.Status);
        var diag = Assert.Single(fit.Diagnostics, d => d.Code == "inverted-segment");
        Assert.Equal("A,B", diag.TargetRef);
        Assert.Equal(FitStatus.Unsatisfiable, fit.Model!.Segments[0].Status);
    }

    [Fact]
    public void ParallelAnchors_SameDepthDistinctDistributions_AllRetained()
    {
        var fit = Engine.Fit(
            new[] { Seg("S1", 0, 3) },
            new[] { Anc("A", 1.0, N(2.0, 0.05)), Anc("B", 1.0, N(2.1, 0.05)), Anc("C", 2.0, N(3.0, 0.05)) },
            new EngineParams());
        Assert.Equal(FitStatus.Ok, fit.Status);
        Assert.Contains(fit.Diagnostics, d => d.Code == "parallel-anchors");
        Assert.DoesNotContain(fit.Diagnostics, d => d.Code == "depth-duplicate");
    }

    [Fact]
    public void DepthDuplicate_SameDepthSameDistribution_Flagged()
    {
        var fit = Engine.Fit(
            new[] { Seg("S1", 0, 3) },
            new[] { Anc("A", 1.0, N(2.0, 0.05)), Anc("A2", 1.0, N(2.0, 0.05)), Anc("C", 2.0, N(3.0, 0.05)) },
            new EngineParams());
        Assert.Equal(FitStatus.Ok, fit.Status);
        Assert.Contains(fit.Diagnostics, d => d.Code == "depth-duplicate" && d.TargetRef!.Contains("A2"));
    }

    [Fact]
    public void LongTailDistribution_KeptAsSamples_NeverMean()
    {
        var samples = new AgeDistribution(AgeDistribution.SamplesKind, "ka", null, null, null, null,
            new[] { 3.95, 4.02, 4.05, 4.08, 4.10, 4.12, 4.15, 4.20, 4.60, 5.30, 6.50 });
        Assert.True(samples.IsLongTail);
        var fit = Engine.Fit(
            new[] { Seg("S1", 0, 3) },
            new[] { Anc("A", 0.5, N(1.0, 0.05)), Anc("T", 2.0, samples) },
            new EngineParams());
        Assert.Equal(FitStatus.Ok, fit.Status);
        Assert.Contains(fit.Diagnostics, d => d.Code == "long-tail" && d.TargetRef == "T");
        // 长尾必须体现在不确定度带上：P95 明显高于中位数
        var seg = fit.Model!.Segments[0];
        var i = Array.FindIndex(seg.BandDepthsM, d => Math.Abs(d - 2.0) < 1e-9);
        Assert.True(seg.P95[i] - seg.P50[i] > 0.5, "long tail must widen the upper uncertainty band");
    }

    [Fact]
    public void MultipleEqualCandidates_AllRetained_WithinTolerance()
    {
        var p = new EngineParams();
        var fit = Engine.Fit(
            new[] { Seg("S1", 0, 3) },
            new[]
            {
                Anc("A", 0.3, N(0.5, 0.06)), Anc("B", 1.2, N(2.30, 0.08)),
                Anc("B2", 1.2, N(2.45, 0.10)), Anc("C", 2.3, N(4.1, 0.15))
            },
            p);
        Assert.Equal(FitStatus.Ok, fit.Status);
        var seg = fit.Model!.Segments[0];
        Assert.True(seg.Candidates.Count >= 2, $"expected >=2 equally-good candidates, got {seg.Candidates.Count}");
        var best = seg.Candidates[0].Score;
        Assert.All(seg.Candidates, c => Assert.True(c.Score >= best - p.EquivalenceLogTolerance));
        Assert.Equal(seg.Candidates.Select(c => c.Id).Distinct().Count(), seg.Candidates.Count);
    }

    [Fact]
    public void BudgetExhausted_IsDistinctResult()
    {
        var fit = Engine.Fit(
            new[] { Seg("S1", 0, 3) },
            new[] { Anc("A", 0.5, N(1, 0.05)), Anc("B", 1.5, N(2, 0.05)), Anc("C", 2.5, N(3, 0.05)) },
            new EngineParams(Budget: 5));
        Assert.Equal(FitStatus.BudgetExhausted, fit.Status);
        Assert.Null(fit.Model);
        Assert.Contains(fit.Diagnostics, d => d.Code == "budget-exhausted");
    }

    [Fact]
    public void EquivalentInputs_ProduceStableOrder()
    {
        AnchorInput[] anchors =
        {
            Anc("A", 0.3, N(0.5, 0.06)), Anc("B", 1.2, N(2.3, 0.08)),
            Anc("B2", 1.2, N(2.45, 0.10)), Anc("C", 2.3, N(4.1, 0.15))
        };
        var p = new EngineParams();
        var f1 = Engine.Fit(new[] { Seg("S1", 0, 3) }, anchors, p);
        var f2 = Engine.Fit(new[] { Seg("S1", 0, 3) }, anchors.Reverse().ToArray(), p);
        Assert.Equal(f1.Model!.InputDigest, f2.Model!.InputDigest);
        Assert.Equal(
            f1.Model.Segments[0].Candidates.Select(c => c.Id).ToArray(),
            f2.Model.Segments[0].Candidates.Select(c => c.Id).ToArray());
    }

    [Fact]
    public void UnconvertibleUnit_StaysInPlace_WithPositionedDiagnostic()
    {
        var bad = new AnchorInput("X", "age-anchor:X", "age-anchor",
            new Quantity(1.0, "fathoms", "mbsf"), N(2.0, 0.05), new Quantity(0, "ka"));
        var fit = Engine.Fit(
            new[] { Seg("S1", 0, 3) },
            new[] { Anc("A", 0.5, N(1, 0.05)), bad, Anc("C", 2.5, N(3, 0.05)) },
            new EngineParams());
        var diag = Assert.Single(fit.Diagnostics, d => d.Code == "unit-unconvertible");
        Assert.Equal("X", diag.TargetRef);
        Assert.Equal("anchor:X/depth", diag.Position);
        Assert.DoesNotContain(fit.Model!.Segments[0].AnchorRefs, r => r == "X");
    }

    [Fact]
    public void MixedUnits_NormalizedOnlyInDerivedLayer()
    {
        var fit = Engine.Fit(
            new[] { Seg("S1", 0, 3) },
            new[]
            {
                Anc("A", 50, N(1, 0.05), depthUnit: "cm"),
                new AnchorInput("B", "age-anchor:B", "age-anchor", new Quantity(2.5, "m", "mbsf"),
                    new AgeDistribution(AgeDistribution.Normal, "yr", 3000, 50, null, null, null), new Quantity(0, "ka"))
            },
            new EngineParams());
        Assert.Equal(FitStatus.Ok, fit.Status);
        var seg = fit.Model!.Segments[0];
        Assert.Contains(seg.BandDepthsM, d => Math.Abs(d - 0.5) < 1e-9);
        Assert.Contains(seg.BandDepthsM, d => Math.Abs(d - 2.5) < 1e-9);
        Assert.All(seg.Rates, r => Assert.True(r.MaxMPerKa > 0));
    }
}
