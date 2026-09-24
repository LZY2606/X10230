using Chrono.Domain;

namespace Chrono.Engine;

/// <summary>
/// 放射性碳校准：常规 BP（14C 年）经储层偏移 ΔR 校正后，通过案例自带曲线映射为 cal BP 分布。
/// 映射保留整个分布（拒绝采样），曲线平台/陡坡自然形成长尾。
/// </summary>
public sealed class Calibrator
{
    private readonly MaterialState _state;

    public Calibrator(MaterialState state) => _state = state;

    public AgeDistribution Resolve(AgeAnchor anchor)
    {
        if (anchor.GivenDistribution is RadiocarbonCalDist rc)
        {
            if (!_state.Reservoirs.TryGetValue(rc.ReservoirId, out var reservoir))
                return new EmpiricalDist(new[] { rc.ConventionalBp });
            _state.Curves.TryGetValue(rc.CurveId, out var curve);
            var offset = reservoir.OffsetYears.Value ?? 0;
            var offsetSigma = reservoir.SigmaYears.Value ?? 0;
            var samples = SampleCalibrated(rc.ConventionalBp, rc.OneSigmaBp, offset, offsetSigma, curve, rc.ParameterVersion);
            return new EmpiricalDist(samples);
        }

        if (anchor.GivenDistribution is not null) return anchor.GivenDistribution;

        if (anchor.ConventionalBp is { } bp)
        {
            var sigma = anchor.OneSigmaBp ?? 0;
            var reservoirId = anchor.ReservoirId ?? "";
            double offset = 0, offsetSigma = 0;
            var curveId = "MARINE23-FIC";
            var pver = _state.Params.Version;
            if (_state.Reservoirs.TryGetValue(reservoirId, out var r))
            {
                offset = r.OffsetYears.Value ?? 0;
                offsetSigma = r.SigmaYears.Value ?? 0;
                curveId = r.MarineCurve;
            }
            _state.Curves.TryGetValue(curveId, out var curve);
            var samples = SampleCalibrated(bp, sigma, offset, offsetSigma, curve, pver);
            return new EmpiricalDist(samples);
        }

        return new GaussianDist(0, 0);
    }

    public static List<double> SampleCalibrated(double carbBp, double carbSigma, double deltaR, double deltaRSigma,
        CalibrationCurve? curve, string parameterVersion)
    {
        var n = 400;
        var samples = new List<double>(n);
        var seed = Canonical.SeedFrom($"cal|{carbBp}|{carbSigma}|{deltaR}|{curve?.Id ?? "none"}|{parameterVersion}");
        var rng = new DeterministicRng(seed);
        var guard = 0;
        while (samples.Count < n && guard < n * 40)
        {
            guard++;
            var measured = carbBp - Gaussian(rng) * carbSigma;
            var marineAge = measured - (deltaR + Gaussian(rng) * deltaRSigma);
            if (curve is not null && curve.TryLookup(marineAge, out var hit))
                samples.Add(hit.calBp);
            else if (curve is null)
                samples.Add(marineAge);
        }
        while (samples.Count < n) samples.Add(carbBp - deltaR);
        return samples;
    }

    private static double Gaussian(DeterministicRng rng)
    {
        var u1 = 1.0 - rng.NextDouble();
        var u2 = 1.0 - rng.NextDouble();
        return Math.Sqrt(-2.0 * Math.Log(u1)) * Math.Cos(2.0 * Math.PI * u2);
    }
}
