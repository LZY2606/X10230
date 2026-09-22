using System.Text.Json;
using System.Text.Json.Serialization;

namespace GeoChron.Core;

/// <summary>
/// 年龄分布采样器。派生层只对可转换分布规范化；任何“以均值替代分布”的路径都被禁止。
/// </summary>
public interface IAgeDistribution
{
    string Kind { get; }
    bool Valid { get; }
    IReadOnlyList<Diagnostic> Diagnostics { get; }
    /// <summary>确定性采样（ka，正值更老）。</summary>
    double[] Sample(int n, ulong seed);
    /// <summary>直接给出有序样本用于展示分位数；长尾由分位数表达，而非均值。</summary>
    double Quantile(double p, int mcBudget, ulong seed);
}

public static class DistributionParser
{
    public static IAgeDistribution Build(AgeDistributionInfo info)
    {
        var diags = new List<Diagnostic>();
        switch (info.Kind.Trim().ToLowerInvariant())
        {
            case "normal":
                if (info.Mean?.IsConvertible != true)
                    diags.Add(new Diagnostic("D001", "mean", "正态分布缺少可转换均值"));
                if (info.Sd?.IsConvertible != true)
                    diags.Add(new Diagnostic("D002", "sd", "正态分布缺少可转换标准差"));
                return diags.Count == 0
                    ? new NormalDist(info.Mean!.Canonical!.Value, info.Sd!.Canonical!.Value)
                    : new InvalidDist("normal", diags);
            case "lognormal":
                if (info.Median?.IsConvertible != true)
                    diags.Add(new Diagnostic("D003", "median", "对数正态分布缺少可转换中位数"));
                if (info.SigmaLog is null or <= 0)
                    diags.Add(new Diagnostic("D004", "sigmaLog", "对数正态分布缺少正的 sigmaLog"));
                return diags.Count == 0
                    ? new LogNormalDist(info.Median!.Canonical!.Value, info.SigmaLog!.Value)
                    : new InvalidDist("lognormal", diags);
            case "uniform":
                if (info.Min?.IsConvertible != true || info.Max?.IsConvertible != true)
                    diags.Add(new Diagnostic("D005", "min/max", "均匀分布缺少可转换端点"));
                else if (info.Min!.Canonical!.Value >= info.Max!.Canonical!.Value)
                    diags.Add(new Diagnostic("D006", "min/max", "均匀分布下界必须小于上界"));
                return diags.Count == 0
                    ? new UniformDist(info.Min!.Canonical!.Value, info.Max!.Canonical!.Value)
                    : new InvalidDist("uniform", diags);
            case "empirical":
                var qs = info.Samples?.Where(q => q.IsConvertible).Select(q => q.Canonical!.Value).OrderBy(v => v).ToArray();
                if (qs is null || qs.Length < 2)
                    diags.Add(new Diagnostic("D007", "samples", "经验分布至少需要两个可转换样本"));
                if (info.Samples is not null && info.Samples.Any(q => !q.IsConvertible))
                    diags.Add(new Diagnostic("D008", "samples", "经验分布含不可转换样本，已保留在原位置并从采样中排除"));
                return diags.Count == 0
                    ? new EmpiricalDist(qs!)
                    : new InvalidDist("empirical", diags);
            case "mixture":
                var members = info.Members?.Select(Build).ToList() ?? new();
                var weights = info.Weights ?? new List<double>();
                if (members.Count < 2) diags.Add(new Diagnostic("D009", "members", "混合分布至少需要两个成分"));
                if (weights.Count != members.Count) diags.Add(new Diagnostic("D010", "weights", "权重数量必须与成分一致"));
                if (weights.Any(w => w < 0)) diags.Add(new Diagnostic("D011", "weights", "权重不能为负"));
                if (weights.Count == members.Count && members.Count > 0 && weights.Sum() <= 0)
                    diags.Add(new Diagnostic("D012", "weights", "权重总和必须为正"));
                return diags.Count == 0
                    ? new MixtureDist(members, weights)
                    : new InvalidDist("mixture", diags);
            default:
                return new InvalidDist(info.Kind,
                    new[] { new Diagnostic("D099", "kind", $"未知分布类型 '{info.Kind}'，原始片段保留") });
        }
    }
}

internal sealed class InvalidDist(string kind, IReadOnlyList<Diagnostic> diagnostics) : IAgeDistribution
{
    public string Kind { get; } = kind;
    public bool Valid => false;
    public IReadOnlyList<Diagnostic> Diagnostics { get; } = diagnostics;
    public double[] Sample(int n, ulong seed) => throw new InvalidOperationException("无效分布不可采样");
    public double Quantile(double p, int mcBudget, ulong seed) => throw new InvalidOperationException("无效分布不可计算分位数");
}

internal abstract record DistBase(string Kind) : IAgeDistribution
{
    public bool Valid => true;
    public IReadOnlyList<Diagnostic> Diagnostics => [];
    public abstract double[] Sample(int n, ulong seed);
    public double Quantile(double p, int mcBudget, ulong seed)
    {
        var s = Sample(mcBudget, seed);
        Array.Sort(s);
        var idx = Math.Clamp((int)Math.Round(p * (s.Length - 1)), 0, s.Length - 1);
        return s[idx];
    }
    protected static Rng Rng(ulong seed) => new(seed);
}

internal sealed record NormalDist(double Mean, double Sd) : DistBase("normal")
{
    public override double[] Sample(int n, ulong seed)
    {
        var rng = Rng(seed);
        var r = new double[n];
        for (var i = 0; i < n; i++) r[i] = Mean + Sd * rng.Gaussian();
        return r;
    }
}

internal sealed record LogNormalDist(double Median, double Sigma) : DistBase("lognormal")
{
    public override double[] Sample(int n, ulong seed)
    {
        var rng = Rng(seed);
        var r = new double[n];
        var mu = Math.Log(Median);
        for (var i = 0; i < n; i++) r[i] = Math.Exp(mu + Sigma * rng.Gaussian());
        return r;
    }
}

internal sealed record UniformDist(double Min, double Max) : DistBase("uniform")
{
    public override double[] Sample(int n, ulong seed)
    {
        var rng = Rng(seed);
        var r = new double[n];
        for (var i = 0; i < n; i++) r[i] = Min + rng.NextDouble() * (Max - Min);
        return r;
    }
}

internal sealed record EmpiricalDist(double[] SortedSamples) : DistBase("empirical")
{
    public override double[] Sample(int n, ulong seed)
    {
        var rng = Rng(seed);
        var r = new double[n];
        for (var i = 0; i < n; i++) r[i] = SortedSamples[rng.NextInt(SortedSamples.Length)];
        return r;
    }
}

internal sealed record MixtureDist(IReadOnlyList<IAgeDistribution> Members, IReadOnlyList<double> Weights) : DistBase("mixture")
{
    public override double[] Sample(int n, ulong seed)
    {
        var total = Weights.Sum();
        var cum = new double[Members.Count];
        var acc = 0.0;
        for (var i = 0; i < Members.Count; i++) { acc += Weights[i] / total; cum[i] = acc; }
        var rng = Rng(seed);
        var draws = Members.Select((m, i) => (m, samples: m.Sample(n, seed ^ (ulong)(i + 1) * 0x9E3779B97F4A7C15UL))).ToArray();
        var r = new double[n];
        for (var i = 0; i < n; i++)
        {
            var u = rng.NextDouble();
            var k = Array.FindIndex(cum, c => u <= c);
            if (k < 0) k = Members.Count - 1;
            r[i] = draws[k].samples[i];
        }
        return r;
    }
}

/// <summary>确定性 SplitMix64 PRNG，保证等价输入可重放、顺序稳定。</summary>
public sealed class Rng(ulong state)
{
    public double NextDouble()
    {
        state += 0x9E3779B97F4A7C15UL;
        var z = state;
        z = (z ^ (z >> 30)) * 0xBF58476D1CE4E5B9UL;
        z = (z ^ (z >> 27)) * 0x94D049BB133111EBUL;
        z ^= z >> 31;
        return (z >> 11) * (1.0 / 9007199254740992.0);
    }
    public int NextInt(int exclusive) => (int)(NextDouble() * exclusive) % exclusive;
    public double Gaussian()
    {
        double u1, u2;
        do { u1 = NextDouble(); } while (u1 <= 1e-15);
        u2 = NextDouble();
        return Math.Sqrt(-2.0 * Math.Log(u1)) * Math.Cos(2.0 * Math.PI * u2);
    }
}
