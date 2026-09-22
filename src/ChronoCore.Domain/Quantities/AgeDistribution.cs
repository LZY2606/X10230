namespace ChronoCore.Domain.Quantities;

/// <summary>
/// 锚点年龄分布。显式样本/分位数全程保留，绝不用单点均值替代。
/// 支持正态、混合（长尾）与离散加权（并列）分布。
/// </summary>
public sealed class AgeDistribution
{
    public required string Id { get; init; }
    public required AgeBasis Basis { get; init; }
    /// <summary>混合分量：N(mean, sd) 权重。单正态即单分量。</summary>
    public required List<DistributionComponent> Components { get; init; }
    /// <summary>确定性样本（BP=1950 年），用于绘图与分位数；为空则按分量采样。</summary>
    public List<double>? ExplicitSamplesYearsBp { get; set; }
    public string? TailNote { get; init; }

    public IReadOnlyList<double> Samples(int n, ulong? seed = null)
    {
        if (ExplicitSamplesYearsBp is { Count: > 0 }) return ExplicitSamplesYearsBp;
        var rng = new DeterministicRng(seed ?? DeterministicRng.SeedFor(Id));
        var total = Components.Sum(c => c.Weight);
        var samples = new double[n];
        for (int i = 0; i < n; i++)
        {
            var pick = rng.NextDouble() * total;
            DistributionComponent chosen = Components[0];
            foreach (var c in Components)
            {
                chosen = c;
                pick -= c.Weight;
                if (pick <= 0) break;
            }
            samples[i] = chosen.MeanYearsBp + chosen.SdYearsBp * rng.NextGaussian();
        }
        return samples;
    }

    public double Quantile(double p, int n = 4001)
    {
        var s = Samples(n).OrderBy(x => x).ToList();
        var idx = Math.Clamp((int)Math.Round(p * (s.Count - 1)), 0, s.Count - 1);
        return s[idx];
    }

    public double Median() => Quantile(0.5);
    public double Q025() => Quantile(0.025);
    public double Q975() => Quantile(0.975);

    /// <summary>
    /// 施加储层偏移：均值平移，不确定度平方和；返回新分布，原分布保留。
    /// 若偏移无法转换，返回原值并附诊断。
    /// </summary>
    public (AgeDistribution Corrected, string? Diagnostic) ApplyReservoir(ReservoirOffset offset)
    {
        if (!offset.DeltaYearsBp.HasValue)
            return (this, offset.Diagnostic);
        var shifted = Components
            .Select(c => c with
            {
                MeanYearsBp = c.MeanYearsBp - offset.DeltaYearsBp.Value,
                SdYearsBp = Math.Sqrt(c.SdYearsBp * c.SdYearsBp + (offset.SdYearsBp ?? 0) * (offset.SdYearsBp ?? 0))
            })
            .ToList();
        return (new AgeDistribution
        {
            Id = Id + "-corr",
            Basis = Basis,
            Components = shifted,
            ExplicitSamplesYearsBp = ExplicitSamplesYearsBp?.Select(a => a - offset.DeltaYearsBp.Value).ToList(),
            TailNote = TailNote
        }, null);
    }
}

public sealed record DistributionComponent(double MeanYearsBp, double SdYearsBp, double Weight = 1.0);

/// <summary>
/// 储层/碳库偏移 R ± σ（年，BP=1950）。原始表示与诊断保留。
/// 海洋碳库效应：真实年龄 = 测量年龄 - R。
/// </summary>
public sealed record ReservoirOffset
{
    public required string Id { get; init; }
    public required string RawText { get; init; }
    public double? DeltaYearsBp { get; init; }
    public double? SdYearsBp { get; init; }
    public required string RawUnit { get; init; }
    public string? Diagnostic { get; init; }

    public static ReservoirOffset Parse(string id, string rawDelta, string? rawSd)
    {
        double? delta = ParseNumber(rawDelta);
        double? sd = rawSd is null ? null : ParseNumber(rawSd);
        string? diag = delta is null
            ? $"储层偏移无法转换：'{rawDelta}'，原始表示保留在 {id}"
            : null;
        return new ReservoirOffset
        {
            Id = id,
            RawText = $"{rawDelta}±{rawSd ?? "?"}",
            DeltaYearsBp = delta,
            SdYearsBp = sd,
            RawUnit = "yr",
            Diagnostic = diag
        };
    }

    private static double? ParseNumber(string raw)
        => double.TryParse(raw.Trim(), System.Globalization.NumberStyles.Float,
            System.Globalization.CultureInfo.InvariantCulture, out var v) ? v : null;
}

/// <summary>确定性 RNG：相同身份/种子产生相同样本（等价输入→稳定顺序与结果）。</summary>
public sealed class DeterministicRng
{
    private ulong _state;

    public DeterministicRng(ulong seed) => _state = seed == 0 ? 0x9E3779B97F4A7C15UL : seed;

    public static ulong SeedFor(string text)
    {
        var bytes = System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(text));
        return BitConverter.ToUInt64(bytes, 0) | 1UL;
    }

    public double NextDouble()
    {
        // SplitMix64
        _state += 0x9E3779B97F4A7C15UL;
        var z = _state;
        z = (z ^ (z >> 30)) * 0xBF58476D1CE4E5B9UL;
        z = (z ^ (z >> 27)) * 0x94D049BB133111EBUL;
        z ^= z >> 31;
        return (z >> 11) * (1.0 / 9007199254740992.0);
    }

    public double NextGaussian()
    {
        // Box-Muller
        double u1, u2;
        do { u1 = NextDouble(); } while (u1 <= 0);
        u2 = NextDouble();
        return Math.Sqrt(-2.0 * Math.Log(u1)) * Math.Cos(2.0 * Math.PI * u2);
    }
}
