using CoreChrono.Domain;

namespace CoreChrono.Engine;

public sealed class CalibrationCurve
{
    public string Name { get; }
    private readonly CalPoint[] _points; // calendar BP ascending (young -> old)

    public CalibrationCurve(string name, IEnumerable<CalPoint> points)
    {
        Name = name;
        _points = points.OrderBy(p => p.CalendarBP).ToArray();
    }

    public double ToCalendarBP(double radiocarbonBP)
    {
        if (_points.Length == 0) return radiocarbonBP;
        if (radiocarbonBP <= _points[0].RadiocarbonBP) return _points[0].CalendarBP;
        if (radiocarbonBP >= _points[^1].RadiocarbonBP) return _points[^1].CalendarBP;
        for (int i = 1; i < _points.Length; i++)
        {
            var a = _points[i - 1]; var b = _points[i];
            if (radiocarbonBP >= a.RadiocarbonBP && radiocarbonBP <= b.RadiocarbonBP)
            {
                var f = (radiocarbonBP - a.RadiocarbonBP) / (b.RadiocarbonBP - a.RadiocarbonBP);
                return a.CalendarBP + f * (b.CalendarBP - a.CalendarBP);
            }
        }
        return _points[^1].CalendarBP;
    }
}

public static class DistributionSampler
{
    public static bool IsLongTail(AgeDistribution d)
        => d.Kind == DistributionKind.MixtureLongTail ||
           (d.Kind == DistributionKind.LogNormal && d.Parameters.Length >= 2 && d.Parameters[1] > 0.45);

    public static double[] SampleCalendar(AgeDistribution d, DeterministicRng rng, int draws)
    {
        var p = d.Parameters;
        var outp = new double[draws];
        switch (d.Kind)
        {
            case DistributionKind.Normal:
                for (int i = 0; i < draws; i++) outp[i] = rng.Normal(p[0], p[1]);
                break;
            case DistributionKind.LogNormal:
                for (int i = 0; i < draws; i++) outp[i] = Math.Exp(rng.Normal(p[0], p[1]));
                break;
            case DistributionKind.Uniform:
                for (int i = 0; i < draws; i++) outp[i] = p[0] + rng.NextDouble() * (p[1] - p[0]);
                break;
            case DistributionKind.MixtureLongTail:
                // parameters: wMain, muMain, sdMain, muTail, sdTail
                for (int i = 0; i < draws; i++)
                    outp[i] = rng.NextDouble() < p[0] ? rng.Normal(p[1], p[2]) : rng.Normal(p[3], p[4]);
                break;
            case DistributionKind.Empirical:
                for (int i = 0; i < draws; i++) outp[i] = p[(int)(rng.NextDouble() * p.Length) % p.Length];
                break;
            default:
                for (int i = 0; i < draws; i++) outp[i] = double.NaN;
                break;
        }
        return outp;
    }

    public static double[] SampleAnchor(ControlPoint cp, ReservoirOffset? res, CalibrationCurve curve, int draws)
    {
        var rng = new DeterministicRng($"anchor|{cp.Id}|{draws}");
        if (cp.AnchorKind == AnchorKind.Radiocarbon && cp.RadiocarbonBP is double rc)
        {
            double sigma = Math.Sqrt(Math.Pow(cp.RadiocarbonSigma ?? 0, 2) + Math.Pow(res?.SigmaR ?? 0, 2));
            double delta = res?.DeltaR ?? 0;
            var cal = new double[draws];
            for (int i = 0; i < draws; i++)
            {
                var rcSample = rng.Normal(rc, sigma) - delta;
                cal[i] = curve.ToCalendarBP(rcSample);
            }
            return cal;
        }
        if (cp.Age is not null) return SampleCalendar(cp.Age, rng, draws);
        return Array.Empty<double>();
    }
}
