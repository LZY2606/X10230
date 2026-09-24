using System.Text.Json;

namespace Chrono.Domain;

/// <summary>
/// 放射性年龄的完整概率分布表示。原始形态（normal/lognormal/mixture/rawSamples）
/// 与原始字符串都会保留；规范化为样本后参与计算。任何情况下都不能只用均值替代。
/// 长尾用 mixture / 显式 samples 承载，分位数与速率区间直接从经验分布得到。
/// </summary>
public sealed record AgeDistribution
{
    public required string Shape { get; init; }            // normal | lognormal | mixture | rawSamples
    public required string RawText { get; init; }          // 导入时原始表示
    public required AgeFrame Frame { get; init; }          // radiocarbon BP / calendar BP1950
    public double? Mean { get; init; }
    public double? Sigma { get; init; }
    public double? Median { get; init; }
    public double[]? RawSamples { get; init; }            // 显式给定的样本（长尾档案用）
    public List<MixtureComponent> Components { get; init; } = new();
    public List<string> Diagnostics { get; init; } = new();
    public string Unit { get; init; } = "yr BP";

    public bool HasContent => Shape is "rawSamples" ? RawSamples is { Length: > 0 } :
        Mean is not null || Components.Count > 0;

    public double MedianOrMean() => Median ?? Mean ?? double.NaN;

    /// <summary>确定性抽样：同一个输入摘要永远得到同一组样本（可重放）。</summary>
    public double[] Sample(int n, long seed)
    {
        var rng = new DeterministicRng(seed);
        var outp = new double[n];
        switch (Shape)
        {
            case "normal" when Mean is { } m && Sigma is { } s:
                for (int i = 0; i < n; i++) outp[i] = m + s * rng.Gaussian();
                break;
            case "lognormal" when Mean is { } m2 && Sigma is { } s2:
            {
                // Mean/Sigma 解释为对数空间的 mu 与 sigma（年龄分布在对数尺度对称）
                for (int i = 0; i < n; i++) outp[i] = Math.Exp(m2 + s2 * rng.Gaussian());
                break;
            }
            case "mixture" when Components.Count > 0:
            {
                var total = Components.Sum(c => c.Weight);
                for (int i = 0; i < n; i++)
                {
                    var pick = rng.Double() * total;
                    var acc = 0.0;
                    foreach (var c in Components)
                    {
                        acc += c.Weight;
                        if (pick <= acc)
                        {
                            if (c.Shape == "lognormal") outp[i] = Math.Exp(c.Mu + c.Sigma * rng.Gaussian());
                            else outp[i] = c.Mu + c.Sigma * rng.Gaussian();
                            break;
                        }
                    }
                }
                break;
            }
            case "rawSamples" when RawSamples is { Length: > 0 } rs:
            {
                // 用确定性重采样（bootstrap）而非直接截断，长尾完整保留
                for (int i = 0; i < n; i++) outp[i] = rs[(int)(rng.Double() * rs.Length) % rs.Length];
                break;
            }
            default:
                for (int i = 0; i < n; i++) outp[i] = Mean ?? double.NaN;
                break;
        }
        return outp;
    }
}

public sealed record MixtureComponent(
    string Shape, double Weight, double Mu, double Sigma,
    double? TailFrom = null, string? Label = null);

/// <summary>储层偏移：本身也是分布（海洋储层效应 ΔR），不能折叠成常数。</summary>
public sealed record ReservoirOffset
{
    public required string Method { get; init; }          // marine | freshwater | none
    public double? DeltaR { get; init; }                  // 年
    public double? DeltaRSigma { get; init; }
    public required string RawText { get; init; }
    public List<string> Diagnostics { get; init; } = new();

    public double[] Sample(int n, long seed)
    {
        var rng = new DeterministicRng(seed);
        var outp = new double[n];
        if (DeltaR is { } dr && DeltaRSigma is { } s)
            for (int i = 0; i < n; i++) outp[i] = dr + s * rng.Gaussian();
        else if (DeltaR is { } dr2)
            Array.Fill(outp, dr2);
        return outp;
    }
}

/// <summary>
/// 确定性伪随机：LCG + Box-Muller。纯函数、跨平台一致，保证“等价输入得到稳定顺序”。
/// </summary>
public sealed class DeterministicRng
{
    private ulong _state;
    public DeterministicRng(long seed) => _state = (ulong)(seed ^ 0x9E3779B97F4A7C15L) + 1;

    public double Double()
    {
        // MMIX LCG 参数
        _state = _state * 6364136223846793005UL + 1442695040888963407UL;
        // xorshift 混合高位
        ulong z = _state;
        z ^= z >> 30; z *= 0xBF58476D1CE4E5B9UL; z ^= z >> 27; z *= 0x94D049BB133111EBUL; z ^= z >> 31;
        return (z >> 11) * (1.0 / 9007199254740992.0); // 2^53
    }

    private double? _spare;
    public double Gaussian()
    {
        if (_spare is { } s) { _spare = null; return s; }
        double u = 0, v = 0;
        while (u is 0.0 || double.IsNaN(u) || double.IsInfinity(u)) { u = Double(); v = Double(); }
        var mag = Math.Sqrt(-2.0 * Math.Log(u));
        _spare = mag * Math.Sin(2.0 * Math.PI * v);
        return mag * Math.Cos(2.0 * Math.PI * v);
    }
}
