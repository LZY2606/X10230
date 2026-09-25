using Chrono.Core.Domain;

namespace Chrono.Core.Engine;

/// <summary>标定/校正参数。版本化，修订即新版本，历史推导按旧版本重放。</summary>
public sealed record CalibrationParams(
    string Version,
    double ReservoirDefaultSigmaYears,
    double CurveOffsetYears,
    double CurveSigmaYears,
    double TieToleranceMeters,
    int MonteCarloSamples,
    IReadOnlyList<string> InterpolationMethods)
{
    public const string LatestVersion = "cal-v2";

    public static CalibrationParams V1() => new(
        "cal-v1",
        ReservoirDefaultSigmaYears: 50,
        CurveOffsetYears: 0,
        CurveSigmaYears: 80,
        TieToleranceMeters: 0.005,
        MonteCarloSamples: 1500,
        InterpolationMethods: new[] { "linear", "isotonic" });

    public static CalibrationParams V2() => V1() with
    {
        Version = "cal-v2",
        CurveSigmaYears = 45,
        MonteCarloSamples = 2400,
    };

    public string ContentHash() => StableId.Fnv1a(
        $"{Version}|{ReservoirDefaultSigmaYears}|{CurveOffsetYears}|{CurveSigmaYears}|{TieToleranceMeters}|{MonteCarloSamples}|{string.Join(",", InterpolationMethods)}").ToString("x16");
}

/// <summary>放射性年龄 + 储层偏移 → 日历年龄完整分布（派生层规范化）。</summary>
public static class Calibrator
{
    public static AgeDist Calibrate(AgeMeasure m, ReservoirOffset? r, CalibrationParams p)
    {
        var reservoirMean = r?.MeanYears ?? 0;
        var reservoirSigma = r is null ? p.ReservoirDefaultSigmaYears : r.SigmaYears;
        var center = m.Years14C - reservoirMean + p.CurveOffsetYears;
        var sigma = Math.Sqrt(m.SigmaYears * m.SigmaYears + reservoirSigma * reservoirSigma + p.CurveSigmaYears * p.CurveSigmaYears);
        return m.LabDistribution.Equals("longtail", StringComparison.OrdinalIgnoreCase)
            ? new StudentTAge(center, sigma, Df: 4.0)
            : new NormalAge(center, sigma);
    }
}
