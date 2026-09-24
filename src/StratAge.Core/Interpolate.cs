namespace StratAge.Core;

public sealed record SegmentCurve(
    List<IntervalRate> Rates, List<UncertaintyBandPoint> Band, double Width, long Ops, bool Tripped);

/// <summary>
/// Builds per-segment age-depth curves. Interpolation NEVER crosses a
/// boundary: each segment is handled independently. Median/p05/p95 curves are
/// propagated from the anchor distributions (no mean replacement).
/// </summary>
public static class Interpolate
{
    public static SegmentCurve SegmentCurve(
        Segment segment, List<EffectiveAnchor> anchors, string mode,
        ModelingParameters p, ref long ops, Func<long, bool> spend)
    {
        var rates = new List<IntervalRate>();
        var band = new List<UncertaintyBandPoint>();
        double width = 0;

        if (anchors.Count == 0 || segment.Top?.Usable != true || segment.Base?.Usable != true)
            return new SegmentCurve(rates, band, 0, 0, false);

        var ordered = anchors.OrderBy(a => a.DepthM).ToList();
        var depths = ordered.Select(a => a.DepthM).ToArray();
        var q = ordered.Select(a =>
        {
            var s = AgeDistribution.ResampleSorted(a.Distribution, p.SampleCount,
                AgeModelBuilder.StreamOf(a.Source.Id));
            return (AgeDistribution.QuantileSorted(s, 0.05),
                    AgeDistribution.QuantileSorted(s, 0.5),
                    AgeDistribution.QuantileSorted(s, 0.95));
        }).ToArray();
        ops += (long)ordered.Count * p.SampleCount;
        if (!spend((long)ordered.Count * p.SampleCount))
            return new SegmentCurve(rates, band, 0, ops, true);

        var p05 = q.Select(x => x.Item1).ToArray();
        var p50 = q.Select(x => x.Item2).ToArray();
        var p95 = q.Select(x => x.Item3).ToArray();

        // Interval sedimentation rates (cm/yr) with uncertainty.
        for (var i = 1; i < depths.Length; i++)
        {
            var dd = depths[i] - depths[i - 1];
            if (dd <= 0) continue;
            // ka per m -> cm/yr : (1 m)/(100000 cm) per (1000 yr) => cm/yr = 100 cm / (ageDiff_ka*1000 yr)
            static double Rate(double ageKa, double depthM) =>
                ageKa <= 0 ? double.NaN : 100.0 / (ageKa * 1000.0);
            var medAge = Math.Abs(p50[i] - p50[i - 1]);
            var loAge = Math.Abs(p05[i] - p95[i - 1]); // steepest plausible
            var hiAge = Math.Abs(p95[i] - p05[i - 1]); // shallowest plausible
            var med = Rate(medAge, dd);
            var rLo = Rate(hiAge, dd);
            var rHi = Rate(loAge, dd);
            rates.Add(new IntervalRate
            {
                Id = $"rate-{segment.Id}-{ordered[i - 1].Source.Id}-{ordered[i].Source.Id}",
                SegmentId = segment.Id,
                TopDepthM = depths[i - 1], BaseDepthM = depths[i],
                MedianCmPerYr = med,
                P05CmPerYr = Math.Min(rLo, rHi),
                P95CmPerYr = Math.Max(rLo, rHi)
            });
        }

        // Uncertainty band on a fine grid between the first and last anchor of
        // this segment only (no extrapolation across a boundary).
        var lo = depths[0];
        var hi = depths[^1];
        const int steps = 24;
        for (var s = 0; s <= steps; s++)
        {
            var depth = lo + (hi - lo) * s / steps;
            band.Add(new UncertaintyBandPoint
            {
                DepthM = depth,
                P05Ka = CurveAt(mode, depths, p05, depth),
                P50Ka = CurveAt(mode, depths, p50, depth),
                P95Ka = CurveAt(mode, depths, p95, depth),
                SegmentId = segment.Id
            });
        }

        // Integrated 90% band width (precision score; smaller = tighter).
        foreach (var b in band) width += Math.Abs(b.P95Ka - b.P05Ka);
        width /= Math.Max(band.Count, 1);

        return new SegmentCurve(rates, band, width, 0, false);
    }

    private static double CurveAt(string mode, double[] x, double[] y, double at)
    {
        if (at <= x[0]) return y[0];
        if (at >= x[^1]) return y[^1];
        return mode == "pchip" ? PchipAt(x, y, at) : LinearAt(x, y, at);
    }

    private static double LinearAt(double[] x, double[] y, double at)
    {
        var i = FindIndex(x, at);
        var t = (at - x[i]) / (x[i + 1] - x[i]);
        return y[i] + t * (y[i + 1] - y[i]);
    }

    private static int FindIndex(double[] x, double at)
    {
        for (var i = 0; i < x.Length - 1; i++)
            if (at >= x[i] && at <= x[i + 1]) return i;
        return x.Length - 2;
    }

    // Monotone cubic Hermite (PCHIP) — preserves monotonicity between controls.
    private static double PchipAt(double[] x, double[] y, double at)
    {
        var n = x.Length;
        var d = new double[n];
        var h = new double[n - 1];
        var sl = new double[n - 1];
        for (var i = 0; i < n - 1; i++)
        {
            h[i] = x[i + 1] - x[i];
            sl[i] = (y[i + 1] - y[i]) / h[i];
        }
        d[0] = sl[0];
        d[n - 1] = sl[n - 2];
        for (var i = 1; i < n - 1; i++)
        {
            if (sl[i - 1] * sl[i] <= 0) d[i] = 0;
            else
            {
                var w1 = 2 * h[i] + h[i - 1];
                var w2 = h[i] + 2 * h[i - 1];
                d[i] = (w1 + w2) / (w1 / sl[i - 1] + w2 / sl[i]);
            }
        }
        var j = FindIndex(x, at);
        var t = (at - x[j]) / h[j];
        var t2 = t * t;
        var t3 = t2 * t;
        var h00 = 2 * t3 - 3 * t2 + 1;
        var h10 = t3 - 2 * t2 + t;
        var h01 = -2 * t3 + 3 * t2;
        var h11 = t3 - t2;
        return h00 * y[j] + h10 * h[j] * d[j] + h01 * y[j + 1] + h11 * h[j] * d[j + 1];
    }
}
