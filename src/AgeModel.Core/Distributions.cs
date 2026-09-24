using System.Text.Json.Serialization;

namespace AgeModel.Core;

public enum DistributionKind { Normal, LogNormal, Empirical }

/// <summary>
/// 锚点年龄的不确定分布。引擎永远消费整组样本，
/// 均值/分位数仅是派生摘要，不能替代分布（业务硬规则 R3）。
/// </summary>
public sealed class AgeDistribution
{
    [JsonPropertyName("kind")] public DistributionKind Kind { get; init; }
    [JsonPropertyName("samples")] public double[] Samples { get; init; } = Array.Empty<double>();
    [JsonPropertyName("unit")] public string Unit { get; init; } = "ka BP";
    [JsonPropertyName("longTail")] public bool LongTail { get; init; }
    [JsonPropertyName("rawDescription")] public string? RawDescription { get; init; }

    public const int StandardSampleCount = 400;

    public static AgeDistribution Normal(double meanKa, double sigmaKa, int seed, string? raw = null, int n = StandardSampleCount)
        => new()
        {
            Kind = DistributionKind.Normal,
            Samples = DeterministicNormal(meanKa, sigmaKa, seed, n),
            LongTail = false,
            RawDescription = raw ?? $"N({meanKa}, {sigmaKa}) ka BP",
        };

    public static AgeDistribution LogNormal(double medianKa, double sigmaLog, int seed, string? raw = null, int n = StandardSampleCount)
    {
        var rng = new DeterministicRng(seed);
        var samples = new double[n];
        var mu = Math.Log(medianKa);
        for (var i = 0; i < n; i++)
        {
            var z = rng.Gaussian();
            samples[i] = Math.Exp(mu + sigmaLog * z);
        }
        Array.Sort(samples);
        return new AgeDistribution
        {
            Kind = DistributionKind.LogNormal,
            Samples = samples,
            LongTail = DetectLongTail(samples),
            RawDescription = raw ?? $"LogNormal(median={medianKa}, sigmaLog={sigmaLog}) ka BP",
        };
    }

    public static AgeDistribution Empirical(double[] sortedSamples, string raw)
    {
        var copy = (double[])sortedSamples.Clone();
        Array.Sort(copy);
        return new AgeDistribution
        {
            Kind = DistributionKind.Empirical,
            Samples = copy,
            LongTail = DetectLongTail(copy),
            RawDescription = raw,
        };
    }

    public double Quantile(double p)
    {
        if (Samples.Length == 0) return double.NaN;
        var idx = Math.Clamp((int)Math.Round(p * (Samples.Length - 1)), 0, Samples.Length - 1);
        return Samples[idx];
    }

    /// <summary>派生摘要：仅供展示与比较，禁止作为模型计算输入。</summary>
    public double Mean => Samples.Length == 0 ? double.NaN : Samples.Average();
    public double Median => Quantile(0.5);
    public double P05 => Quantile(0.05);
    public double P95 => Quantile(0.95);

    private static bool DetectLongTail(double[] sorted)
    {
        if (sorted.Length < 20) return false;
        var q50 = QuantileOf(sorted, 0.5);
        var q75 = QuantileOf(sorted, 0.75);
        var q95 = QuantileOf(sorted, 0.95);
        var iqr = q75 - q50;
        if (iqr <= 0) return false;
        var tailCount = sorted.Count(v => v > q95 + 1.5 * iqr);
        return tailCount > sorted.Length * 0.01;
    }

    private static double QuantileOf(double[] s, double p)
        => s[Math.Clamp((int)Math.Round(p * (s.Length - 1)), 0, s.Length - 1)];

    internal static double[] DeterministicNormal(double mean, double sd, int seed, int n)
    {
        var rng = new DeterministicRng(seed);
        var samples = new double[n];
        for (var i = 0; i < n; i++) samples[i] = mean + sd * rng.Gaussian();
        Array.Sort(samples);
        return samples;
    }
}

/// <summary>Box-Muller 的确定性 RNG：等价输入（含 seed）→ 完全一致的样本序列与稳定排序。</summary>
internal sealed class DeterministicRng
{
    private ulong _state;
    public DeterministicRng(int seed) => _state = unchecked((ulong)seed * 6364136223846793005UL + 1442695040888963407UL);

    public double NextUnit()
    {
        _state ^= _state << 13;
        _state ^= _state >> 7;
        _state ^= _state << 17;
        return (_state >> 11) * (1.0 / 9007199254740992.0);
    }

    public double Gaussian()
    {
        double u1, u2;
        do { u1 = Math.Max(NextUnit(), 1e-15); } while (u1 <= 0);
        u2 = NextUnit();
        return Math.Sqrt(-2.0 * Math.Log(u1)) * Math.Cos(2.0 * Math.PI * u2);
    }
}
