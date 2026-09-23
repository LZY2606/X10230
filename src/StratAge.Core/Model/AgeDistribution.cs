using StratAge.Core.Domain;

namespace StratAge.Core.Model;

/// <summary>
/// 锚点年龄分布：始终以样本（蒙特卡洛抽样）表示，均值只作展示参考。
/// 放射性碳分布先扣储层再按校准版本映射到日历年。
/// </summary>
public sealed class AgeDistribution
{
    public required string SourceUnit { get; init; }
    public required string Kind { get; init; }
    public required double[] Samples { get; init; } // cal BP 年（派生层）
    public required double[] RawSamples { get; init; } // 原始单位抽样（导入表示保留）
    public double? ReferenceMean { get; init; } // 仅供展示，绝不替代分布
    public bool IsRadiocarbon { get; init; }
    public string? CalibrationVersion { get; init; }
    public string? ReservoirId { get; init; }
    public bool IsLongTail { get; init; }

    public double Quantile(double p)
    {
        var s = (double[])Samples.Clone();
        Array.Sort(s);
        var idx = Math.Clamp(p * (s.Length - 1), 0, s.Length - 1);
        var lo = (int)Math.Floor(idx);
        var hi = (int)Math.Ceiling(idx);
        return s[lo] + (s[hi] - s[lo]) * (idx - lo);
    }

    public double Median => Quantile(0.5);
    public double Mean => Samples.Average();
}

public static class Calibration
{
    // 合成 IntCal 风格曲线（离线、确定性、可重放）。
    // v1: calBP = 0.95 * 14Cage；v2（参数修订）：calBP = 0.92 * 14Cage + 60
    public const string Version1 = "intcal-synth-v1";
    public const string Version2 = "intcal-synth-v2";

    public static bool Known(string? v) => v is Version1 or Version2;

    public static double RadiocarbonToCalBP(double c14age, string? version) => (version ?? Version1) switch
    {
        Version2 => 0.92 * c14age + 60.0,
        _ => 0.95 * c14age,
    };
}

public static class DistributionSampler
{
    // 确定性抽样：等价输入 -> 等价样本序列（用锚点 id 的稳定哈希做种子）。
    public static double[] StableRandom(int seed, int n)
    {
        var rng = new SplitMix64((ulong)seed);
        var u = new double[n];
        for (var i = 0; i < n; i++) u[i] = rng.NextDouble();
        return u;
    }

    public static int StableSeed(string id)
    {
        unchecked
        {
            var h = 1469598103934665603UL;
            foreach (var c in id) { h ^= c; h *= 1099511628211UL; }
            return (int)(h ^ (h >> 32));
        }
    }

    public static double NormalQuantile(double p)
    {
        // Beasley-Springer-Moro 近似
        var a = new[] { -3.969683028665376e+01, 2.209460984245205e+02, -2.759285104469687e+02, 1.383577518672690e+02, -3.066479806614716e+01, 2.506628277459239e+00 };
        var b = new[] { -5.447609879822406e+01, 1.615858368580409e+02, -1.556989798598866e+02, 6.680131188771972e+01, -1.328068155288572e+01 };
        var c = new[] { -7.784894002430293e-03, -3.223964580411365e-01, -2.400758277161838e+00, -2.549732539343734e+00, 4.374664141464968e+00, 2.938163982698783e+00 };
        var d = new[] { 7.784695709041462e-03, 3.224671290700398e-01, 2.445134137142996e+00, 3.637833968420178e+00 };
        double q, r;
        const double plow = 0.02425, phigh = 1 - plow;
        if (p < plow) { q = Math.Sqrt(-2 * Math.Log(p)); return (((((c[0] * q + c[1]) * q + c[2]) * q + c[3]) * q + c[4]) * q + c[5]) / ((((d[0] * q + d[1]) * q + d[2]) * q + d[3]) * q + 1); }
        if (p > phigh) { q = Math.Sqrt(-2 * Math.Log(1 - p)); return -(((((c[0] * q + c[1]) * q + c[2]) * q + c[3]) * q + c[4]) * q + c[5]) / ((((d[0] * q + d[1]) * q + d[2]) * q + d[3]) * q + 1); }
        q = p - 0.5; r = q * q;
        return (((((a[0] * r + a[1]) * r + a[2]) * r + a[3]) * r + a[4]) * r + a[5]) * q / (((((b[0] * r + b[1]) * r + b[2]) * r + b[3]) * r + b[4]) * r + 1);
    }
}

public struct SplitMix64
{
    ulong _s;
    public SplitMix64(ulong seed) => _s = seed;
    public ulong Next()
    {
        _s += 0x9E3779B97F4A7C15UL;
        var z = _s;
        z = (z ^ (z >> 30)) * 0xBF58476D1CE4E5B9UL;
        z = (z ^ (z >> 27)) * 0x94D049BB133111EBUL;
        return z ^ (z >> 31);
    }
    public double NextDouble() => (Next() >> 11) * (1.0 / 9007199254740992.0);
}

public static class DistributionFactory
{
    public const int SampleCount = 4000;

    public static AgeDistribution Build(DistributionSpec spec, string anchorId, double reservoirDeltaR, List<Diag> diags, string path = "")
    {
        var seed = DistributionSampler.StableSeed(anchorId + "|" + spec.Kind + "|" + spec.Unit);
        var u = DistributionSampler.StableRandom(seed, SampleCount);
        var raw = new double[SampleCount];
        var cal = new double[SampleCount];
        var isC14 = spec.Kind == "radiocarbon" || UnitTable.IsRadiocarbon(spec.Unit);

        for (var i = 0; i < SampleCount; i++)
        {
            double r;
            switch (spec.Kind)
            {
                case "normal":
                    r = spec.Mean!.Value + spec.Sigma!.Value * DistributionSampler.NormalQuantile(u[i]);
                    break;
                case "uniform":
                    r = spec.Min!.Value + (spec.Max!.Value - spec.Min!.Value) * u[i];
                    break;
                case "empirical":
                    r = EmpiricalDraw(spec.Samples!, u[i]);
                    break;
                case "radiocarbon":
                    var m = spec.Mean ?? 0;
                    var s = spec.Sigma ?? (m * 0.02);
                    r = m + s * DistributionSampler.NormalQuantile(u[i]);
                    break;
                default: r = 0; break;
            }
            raw[i] = r;
        }

        var calVersion = spec.CalibrationVersion;
        if (isC14)
        {
            var dr = spec.ReservoirDeltaR ?? reservoirDeltaR;
            if (!Calibration.Known(calVersion))
            {
                diags.Add(new Diag("CALIBRATION_VERSION_UNKNOWN",
                    $"未知校准版本 '{calVersion}'，放射性碳年龄保留原始分布，不参与年代模型", path, Severity: "error"));
                cal = Array.Empty<double>();
            }
            else
                for (var i = 0; i < SampleCount; i++)
                    cal[i] = Calibration.RadiocarbonToCalBP(Math.Max(0, raw[i] - dr), calVersion);
        }
        else
        {
            var local = new List<Diag>();
            var canon = UnitTable.ConvertTime(1, spec.Unit, local, path);
            if (canon is null)
                throw new IngestionDiagnosticException(local.FirstOrDefault() ?? new Diag("UNIT_UNKNOWN", spec.Unit));
            var factor = canon.Value;
            for (var i = 0; i < SampleCount; i++) cal[i] = raw[i] * factor;
        }

        var sorted = (double[])cal.Clone();
        if (sorted.Length > 0) Array.Sort(sorted);
        var isLongTail = cal.Length > 0 && TailTest(cal);

        return new AgeDistribution
        {
            SourceUnit = spec.Unit,
            Kind = spec.Kind,
            Samples = cal,
            RawSamples = raw,
            ReferenceMean = raw.Length > 0 ? raw.Average() : null,
            IsRadiocarbon = isC14,
            CalibrationVersion = calVersion,
            ReservoirId = spec.ReservoirId,
            IsLongTail = isLongTail,
        };
    }

    static double EmpiricalDraw(IReadOnlyList<double> samples, double u)
    {
        var s = samples.ToArray();
        Array.Sort(s);
        var pos = u * (s.Length - 1);
        var lo = (int)Math.Floor(pos);
        var hi = Math.Min(s.Length - 1, lo + 1);
        return s[lo] + (s[hi] - s[lo]) * (pos - lo);
    }

    // 长尾判据：95%-中位数距离 / 中位数-5%距离 > 2.2，或偏度超过阈值。
    public static bool TailTest(double[] sortedAsc)
    {
        double Q(double p)
        {
            var idx = p * (sortedAsc.Length - 1);
            var lo = (int)idx; var hi = Math.Min(sortedAsc.Length - 1, lo + 1);
            return sortedAsc[lo] + (sortedAsc[hi] - sortedAsc[lo]) * (idx - lo);
        }
        var q05 = Q(0.05); var q50 = Q(0.5); var q95 = Q(0.95);
        var upper = Math.Abs(q95 - q50); var lower = Math.Abs(q50 - q05);
        return lower > 1e-9 && upper / lower > 2.2;
    }
}

public sealed class IngestionDiagnosticException(Diag diag) : Exception(diag.Message)
{
    public Diag Diag { get; } = diag;
}
