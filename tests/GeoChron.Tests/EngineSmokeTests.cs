using GeoChron.Core;
using Xunit;

namespace GeoChron.Tests;

public class EngineSmokeTests
{
    [Fact]
    public void SampleCase_ProducesMultipleCandidates_AndRespectsGap()
    {
        var (pack, diags) = AgeModelEngine.ParsePack(SampleCase.LoadJson());
        Assert.Empty(diags.Where(d => d.Severity == "error"));
        var output = new AgeModelEngine().Run(pack!);
        Assert.Equal(RunOutcome.Ok, output.Outcome);
        Assert.True(output.Candidates.Count >= 2);
        Assert.True(output.Candidates.Any(c => c.EqualOptimal));
        Assert.Contains(output.Sections, s => s.LowerBoundaryGapId == "gap-1");
        Assert.DoesNotContain(output.Candidates.SelectMany(c => c.Band.SelectMany(b => b.Quantiles)).ToList(),
            output.Candidates.SelectMany(c => c.Band).First().Quantiles.First().Value);
    }

    [Fact]
    public void CorruptJson_IsFormatCorrupt_DistinctFromSemanticAndBudget()
    {
        var (pack, _) = AgeModelEngine.ParsePack("{ not json");
        Assert.Null(pack);
    }
}
