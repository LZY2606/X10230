using System.Text.Json.Serialization;

namespace Chrono.Domain;

/// <summary>
/// 锚点年龄分布（cal BP，年）。分布是一等公民：任何派生结果都基于抽样传播，
/// 绝不用单一均值替代分布。
/// </summary>
[JsonPolymorphic(TypeDiscriminatorPropertyName = "kind")]
[JsonDerivedType(typeof(GaussianDist), "gaussian")]
[JsonDerivedType(typeof(SkewGaussianDist), "skew")]
[JsonDerivedType(typeof(MixtureDist), "mixture")]
[JsonDerivedType(typeof(EmpiricalDist), "empirical")]
[JsonDerivedType(typeof(RadiocarbonCalDist), "radiocarbon")]
public abstract record AgeDistribution
{
    public abstract string Kind { get; }

    /// <summary>确定性伪随机抽样。相同种子（由候选身份派生）→ 相同样本。</summary>
    public abstract double Sample(DeterministicRng rng);

    /// <summary>理论或数值众数，仅用于展示与排序，不用于传播。</summary>
    public abstract double ModeApprox();
}

public sealed record GaussianDist(double Mean, double Sigma) : AgeDistribution
{
    public override string Kind => "gaussian";
    public override double Sample(DeterministicRng rng)
    {
        var u1 = 1.0 - rng.NextDouble();
        var u2 = 1.0 - rng.NextDouble();
        var z = Math.Sqrt(-2.0 * Math.Log(u1)) * Math.Cos(2.0 * Math.PI * u2);
        return Mean + Sigma * z;
    }
    public override double ModeApprox() => Mean;
}

/// <summary>偏斜高斯（对数正态），用于年龄分布长尾。</summary>
public sealed record SkewGaussianDist(double LogMean, double LogSigma) : AgeDistribution
{
    public override string Kind => "skew";
    public override double Sample(DeterministicRng rng)
    {
        var u1 = 1.0 - rng.NextDouble();
        var u2 = 1.0 - rng.NextDouble();
        var z = Math.Sqrt(-2.0 * Math.Log(u1)) * Math.Cos(2.0 * Math.PI * u2);
        return Math.Exp(LogMean + LogSigma * z);
    }
    public override double ModeApprox() => Math.Exp(LogMean - LogSigma * LogSigma);
}

/// <summary>混合分布：多峰/长尾的通用表示，权重显式保留。</summary>
public sealed record MixtureDist(IReadOnlyList<AgeDistribution> Components, IReadOnlyList<double> Weights) : AgeDistribution
{
    public override string Kind => "mixture";
    public override double Sample(DeterministicRng rng)
    {
        var pick = rng.NextDouble();
        var acc = 0.0;
        for (var i = 0; i < Components.Count; i++)
        {
            acc += Weights[i];
            if (pick <= acc) return Components[i].Sample(rng);
        }
        return Components[^1].Sample(rng);
    }
    public override double ModeApprox() =>
        Components.OrderByDescending((c, i) => Weights[i]).First().ModeApprox();
}

/// <summary>经验样本分布（校准输出/历史档案原样保留）。</summary>
public sealed record EmpiricalDist(IReadOnlyList<double> Samples) : AgeDistribution
{
    public override string Kind => "empirical";
    public override double Sample(DeterministicRng rng)
        => Samples[(int)(rng.NextDouble() * Samples.Length) % Samples.Length];
    public override double ModeApprox()
    {
        var sorted = Samples.OrderBy(x => x).ToList();
        return sorted[sorted.Count / 2];
    }
}

/// <summary>
/// 放射性碳结果经校准曲线与储层偏移后的年龄分布（延迟采样时才真正计算，
/// 使“参数修订 → 局部重算”可被观察到）。
/// </summary>
public sealed record RadiocarbonCalDist(string LabCode, double ConventionalBp, double OneSigmaBp,
    string ReservoirId, string CurveId, string ParameterVersion) : AgeDistribution
{
    public override string Kind => "radiocarbon";
    public override double Sample(DeterministicRng rng) =>
        throw new InvalidOperationException("放射性碳分布必须由校准器解析后抽样");
    public override double ModeApprox() => ConventionalBp;
}

/// <summary>确定性 LCG，抽样可重放、跨进程一致。</summary>
public sealed class DeterministicRng
{
    private ulong _state;

    public DeterministicRng(ulong seed) => _state = seed == 0 ? 0x9E3779B97F4A7C15UL : seed;

    public double NextDouble()
    {
        // splitmix64
        _state += 0x9E3779B97F4A7C15UL;
        var z = _state;
        z = (z ^ (z >> 30)) * 0xBF58476D1CE4E5B9UL;
        z = (z ^ (z >> 27)) * 0x94D049BB133111EBUL;
        z ^= z >> 31;
        return (z >> 11) * (1.0 / 9007199254740992.0);
    }
}
