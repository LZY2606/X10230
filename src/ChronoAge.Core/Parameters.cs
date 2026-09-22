namespace ChronoAge.Core;

/// <summary>
/// Versioned modelling parameters. Every derivation pins the parameter version in effect;
/// a revision (ParametersSet event) never rewrites older derivations.
/// </summary>
public sealed record ModelParameters(
    string Version,
    CalibrationTable? Calibration,
    int SamplesPerAnchor,
    long SampleBudget,
    int MaxCandidates,
    double PlausibleRateMin,
    double PlausibleRateMax,
    long RngSeed,
    double DepthEpsilonM = 0.02)
{
    public const string DefaultVersion = "rules/v1.0";

    public static ModelParameters Default => new(
        DefaultVersion,
        CalibrationTable.DefaultTable,
        SamplesPerAnchor: 400,
        SampleBudget: 200_000,
        MaxCandidates: 64,
        PlausibleRateMin: 0.5,
        PlausibleRateMax: 200.0,
        RngSeed: 20260923L,
        DepthEpsilonM: 0.02);
}

/// <summary>
/// Offline deterministic radiocarbon calibration table. The table itself is a versioned
/// source; radiocarbon measurements are never silently treated as calendar years.
/// </summary>
public sealed record CalibrationTable(string Id, IReadOnlyList<CalibPoint> Points)
{
    public static readonly CalibrationTable DefaultTable = new("intcal-lite/v1", new[]
    {
        new CalibPoint(0, 0, 30),
        new CalibPoint(1000, 900, 45),
        new CalibPoint(2000, 1900, 55),
        new CalibPoint(3000, 3150, 70),
        new CalibPoint(4000, 4400, 80),
        new CalibPoint(5000, 5700, 90),
        new CalibPoint(6000, 6850, 95),
        new CalibPoint(8000, 8800, 110),
        new CalibPoint(10000, 11400, 140),
        new CalibPoint(12000, 13900, 170),
        new CalibPoint(15000, 18200, 220),
        new CalibPoint(20000, 24000, 300),
    });

    public bool TryMap(double rcAge, out double calMu, out double calSigma)
    {
        calMu = rcAge; calSigma = 0;
        if (Points.Count == 0) return false;
        if (rcAge <= Points[0].RcAge) { calMu = Points[0].CalMu; calSigma = Points[0].CalSigma; return true; }
        for (var i = 1; i < Points.Count; i++)
        {
            if (rcAge <= Points[i].RcAge)
            {
                var a = Points[i - 1]; var b = Points[i];
                var t = (rcAge - a.RcAge) / (b.RcAge - a.RcAge);
                calMu = a.CalMu + t * (b.CalMu - a.CalMu);
                calSigma = a.CalSigma + t * (b.CalSigma - a.CalSigma);
                return true;
            }
        }
        calMu = Points[^1].CalMu; calSigma = Points[^1].CalSigma;
        return true;
    }
}

public sealed record CalibPoint(double RcAge, double CalMu, double CalSigma);
