using System.Text.Json.Serialization;

namespace StratAge.Core;

/// <summary>
/// Derived (normalised) representation of one anchor. The source distribution
/// is kept intact; reservoir offset is applied by convolution (variance is
/// added), never by replacing the distribution with a single mean.
/// </summary>
public sealed record EffectiveAnchor
{
    public required AgeAnchor Source { get; init; }
    public required double DepthM { get; init; }
    public required AgeDistribution Distribution { get; init; }
    public required bool LongTail { get; init; }
    public required string SegmentId { get; init; }
    public string? TieGroup => Source.TieGroup;
}

public static class AgeModelBuilder
{
    private static bool IsLong(AgeDistribution d) => d switch
    {
        GaussianAge => false,
        StudentTAge t => t.LongTail,
        EmpiricalAge e => e.LongTail,
        _ => false
    };

    /// <summary>
    /// Builds the effective ka BP distribution. A marine reservoir offset is
    /// subtracted (calendar = measured − reservoir) and its uncertainty is
    /// convolved; the long-tailed/empirical shape is preserved.
    /// </summary>
    public static EffectiveAnchor? TryBuild(AgeAnchor a, NormalizedCase n, ModelingParameters p,
        List<Diagnostic> diags)
    {
        if (a.Excluded) return null;
        if (a.Depth is null || !a.Depth.Usable || a.Age is null || !a.Age.Usable) return null;

        var loc = $"anchors/{a.Id}";
        AgeDistribution dist = a.Distribution
            ?? (a.AgeSigma?.Usable == true
                ? new GaussianAge { MeanKa = a.Age.Canonical!.Value, SigmaKa = a.AgeSigma.Canonical!.Value }
                : new GaussianAge { MeanKa = a.Age.Canonical!.Value, SigmaKa = 0.0 });

        // An event bed may supply the age distribution for a correlated tephra.
        if (a.Distribution is null && a.EventBedId is not null)
        {
            var eb = n.Case.EventBeds.FirstOrDefault(e => e.Id == a.EventBedId);
            if (eb is not null && eb.Age?.Usable == true)
            {
                var sig = eb.AgeSigma?.Usable == true ? eb.AgeSigma.Canonical!.Value : 0.0;
                dist = new GaussianAge { MeanKa = eb.Age.Canonical!.Value, SigmaKa = sig };
            }
        }

        if (p.ApplyReservoir && a.ReservoirOffsetId is not null &&
            n.Reservoir.TryGetValue(a.ReservoirOffsetId, out var ro))
        {
            dist = ApplyReservoir(dist, ro, a.Id);
            diags.Add(Normalizer.Diag(Severity.Info, $"{loc}/reservoirOffsetId", "RESERVOIR_APPLIED",
                $"Reservoir offset {ro.Id} ({ro.Raw}) convolved into the distribution of {a.Id}; mean shifted by −{ro.DeltaRKa} ka, variance added.",
                "R-RESERVOIR", a.Id));
        }

        return new EffectiveAnchor
        {
            Source = a, DepthM = a.Depth.Canonical!.Value, Distribution = dist,
            LongTail = IsLong(dist), SegmentId = a.SegmentId
        };
    }

    private static AgeDistribution ApplyReservoir(AgeDistribution d, ReservoirOffset ro, string anchorId)
    {
        var stream = StreamOf(anchorId);
        var n = 4096;
        var meas = d.Sample(n, stream);
        var res = ro.Distribution().Sample(n, stream ^ 0x5A5AUL);
        var corrected = new double[n];
        for (var i = 0; i < n; i++)
            corrected[i] = meas[i] - res[i];
        return new EmpiricalAge { SamplesKa = corrected };
    }

    internal static ulong StreamOf(string s)
    {
        ulong h = 1469598103934665603UL;
        foreach (var c in s)
        {
            h ^= c;
            h *= 1099511628211UL;
        }
        return h;
    }
}
