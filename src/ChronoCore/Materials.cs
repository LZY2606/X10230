using System.Text.Json;
using System.Text.Json.Serialization;

namespace ChronoCore;

/// <summary>
/// 锚点的完整年龄分布。任何派生层都不得只用一个均值替代分布：
/// 不确定度带、速率区间都从分布重采样得到。
/// </summary>
[JsonPolymorphic(TypeDiscriminatorPropertyName = "kind")]
[JsonDerivedType(typeof(NormalAgeDist), "normal")]
[JsonDerivedType(typeof(LogNormalAgeDist), "lognormal")]
[JsonDerivedType(typeof(EmpiricalAgeDist), "empirical")]
[JsonDerivedType(typeof(MixtureAgeDist), "mixture")]
public abstract record AgeDistribution
{
    public abstract double Mean();
    /// <summary>CDF 的逆：p∈(0,1) 时返回该分位年龄(ka BP)。</summary>
    public abstract double Quantile(double p);
    /// <summary>以确定性 RNG 抽样（用于可重放的不确定度带）。</summary>
    public abstract double Sample(System.Random rng);

    /// <summary>长尾标记：混合物中存在次权重但年龄显著更大的分量。</summary>
    [JsonIgnore] public virtual bool HasLongTail => false;

    protected static double NormalQ(double p) => Gaussian.InverseCdf(p);
}

public sealed record NormalAgeDist(double Mu, double Sigma) : AgeDistribution
{
    public override double Mean() => Mu;
    public override double Quantile(double p) => Mu + Sigma * NormalQ(p);
    public override double Sample(System.Random rng)
        => Mu + Sigma * Gaussian.Sample(rng);
}

/// <summary>对数正态：天然适合带长尾的放射性年龄分布。</summary>
public sealed record LogNormalAgeDist(double Median, double SigmaLog) : AgeDistribution
{
    public override double Mean() => Median * Math.Exp(SigmaLog * SigmaLog / 2.0);
    public override double Quantile(double p)
        => Median * Math.Exp(SigmaLog * NormalQ(p));
    public override double Sample(System.Random rng)
        => Median * Math.Exp(SigmaLog * Gaussian.Sample(rng));
    public override bool HasLongTail => SigmaLog >= 0.9;
}

/// <summary>经验分布：保留原始测量值（如多次制样/实验室结果）。</summary>
public sealed record EmpiricalAgeDist(List<double> SortedSamples) : AgeDistribution
{
    public override double Mean() => SortedSamples.Average();
    public override double Quantile(double p)
    {
        var s = SortedSamples.OrderBy(x => x).ToList();
        if (s.Count == 0) throw new InvalidOperationException("empty empirical distribution");
        var idx = Math.Clamp(p * (s.Count - 1), 0, s.Count - 1);
        var lo = (int)Math.Floor(idx);
        var hi = (int)Math.Ceiling(idx);
        return s[lo] + (s[hi] - s[lo]) * (idx - lo);
    }
    public override double Sample(System.Random rng)
        => SortedSamples[rng.Next(SortedSamples.Count)];
}

/// <summary>混合分布：主分量 + 老年长尾分量（继承/再沉积污染）。</summary>
public sealed record MixtureAgeDist(List<MixComponent> Components) : AgeDistribution
{
    public override double Mean() => Components.Sum(c => c.Weight * c.Dist.Mean());
    public override double Quantile(double p)
    {
        // 反演混合 CDF：在分量均值区间上做有界二分。
        double lo = Components.Min(c => c.Dist.Quantile(1e-6));
        double hi = Components.Max(c => c.Dist.Quantile(1 - 1e-6));
        for (var i = 0; i < 80; i++)
        {
            var mid = (lo + hi) / 2;
            var cdf = Components.Sum(c => c.Weight * GaussianNormalCdfOfComponent(c, mid));
            if (cdf < p) lo = mid; else hi = mid;
        }
        return (lo + hi) / 2;
    }
    public override double Sample(System.Random rng)
    {
        var u = rng.NextDouble();
        var acc = 0.0;
        foreach (var c in Components)
        {
            acc += c.Weight;
            if (u <= acc) return c.Dist.Sample(rng);
        }
        return Components[^1].Dist.Sample(rng);
    }
    public override bool HasLongTail
    {
        get
        {
            if (Components.Count < 2) return false;
            var main = Components.MaxBy(c => c.Weight)!;
            return Components.Any(c => c.Weight < main.Weight
                && c.Dist.Mean() > main.Dist.Mean() * 1.25);
        }
    }

    private static double GaussianNormalCdfOfComponent(MixComponent c, double x)
    {
        // 仅对正态/对数正态分量用闭式 CDF；其它分量走分位反查近似。
        switch (c.Dist)
        {
            case NormalAgeDist n:
                return Gaussian.Cdf((x - n.Mu) / n.Sigma);
            case LogNormalAgeDist ln:
                return Gaussian.Cdf((Math.Log(x / ln.Median)) / ln.SigmaLog);
            default:
                // 数值反演近似
                double lo = 0, hi = 1;
                for (var i = 0; i < 40; i++)
                {
                    var m = (lo + hi) / 2;
                    if (c.Dist.Quantile(m) < x) lo = m; else hi = m;
                }
                return (lo + hi) / 2;
        }
    }
}

public sealed record MixComponent(double Weight, AgeDistribution Dist);

/// <summary>储层偏移（海洋/碳库效应）：带自身不确定性，作用于放射性年龄。</summary>
public sealed record ReservoirCorrection(double DeltaRKa, double SigmaKa, string Basis, string? Raw);

public enum AnchorKind { Radiocarbon, Tephra, Biohorizon, Magneto, Tuning, Manual }

/// <summary>深度年龄锚点：深度、原始年龄分布、储层偏移与原始表示都保留。</summary>
public sealed record DepthAnchor(
    string Id,
    string CoreId,
    double DepthM,
    AgeDistribution RawAge,
    AnchorKind Kind,
    string? TimeBasis,
    ReservoirCorrection? Reservoir,
    string? RawDepthText,
    string? RawAgeText,
    string? SourceRef,
    bool Excluded = false,
    string? ExclusionId = null)
{
    /// <summary>校正后年龄分布（应用储层偏移，仅派生层执行，原始分布保留）。</summary>
    public AgeDistribution CorrectedAge()
    {
        if (Reservoir is null) return RawAge;
        return RawAge switch
        {
            NormalAgeDist n => new NormalAgeDist(n.Mu + Reservoir.DeltaRKa,
                Math.Sqrt(n.Sigma * n.Sigma + Reservoir.SigmaKa * Reservoir.SigmaKa)),
            LogNormalAgeDist ln => new MixtureAgeDist(new List<MixComponent>
            {
                new(1.0, new NormalAgeDist(ln.Median + Reservoir.DeltaRKa, ln.SigmaLog * ln.Median))
            }),
            _ => RawAge
        };
    }
}

public enum GapKind { Hiatus, CoreGap, Unconformity }

/// <summary>显式深度边界：缺口或不整合。年龄只能在连续层段内单调，不得跨边界插值。</summary>
public sealed record DepthGap(
    string Id, string CoreId, double TopM, double BottomM, GapKind Kind,
    string? AgeTopKa, string? AgeBottomKa, string Basis, string? Raw);

/// <summary>倒置层段（滑塌/再沉积/构造倒转）：显式标记，不参与正常单调约束。</summary>
public sealed record Inversion(string Id, string CoreId, double FromM, double ToM,
    string Reason, string? Raw);

/// <summary>事件层（火山灰/地磁倒转/生物事件）：跨孔相关关系是材料，不是派生量。</summary>
public sealed record EventBed(
    string Id, string CoreId, double DepthM, string Name,
    string? CorrelatedEventId, double? EventAgeKa, double? EventAgeSigmaKa,
    string CorrelationBasis, string? Raw);

/// <summary>排除决定属于不可修改的决定层：一旦作出即作为事件保留，只能再发新决定撤销。</summary>
public sealed record Exclusion(
    string Id, string TargetAnchorId, string Reason, string DecidedBy,
    string AtEventId, bool Rejected = false, string? RevokedByEventId = null);

/// <summary>模型参数版本：任何参数修改都产生新版本并作为事件保留。</summary>
public sealed record ModelParameters(
    int Version,
    double MonteCarloDraws,
    int MaxCandidates,
    double TailProbabilityLow,
    double TailProbabilityHigh,
    string InterpolationPolicy,
    string ChangedBy,
    string? Note)
{
    public const string PolicyAllEqualBest = "keep-all-equal-best";
    public const string PolicyFastest = "fastest-only";

    public static ModelParameters Initial => new(1, 400, 64, 0.05, 0.95, PolicyAllEqualBest, "system", "初始参数版本");
}
