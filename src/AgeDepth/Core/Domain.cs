using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace AgeDepth.Core;

/// <summary>诊断：带位置的输入问题。输入不完整时返回诊断，绝不用空值/默认值伪装成功。</summary>
public sealed record Diagnostic(string Code, string Message, string Path, string Severity);

/// <summary>
/// 领域量：始终携带单位与坐标/时间基准；导入时保留原始表示 Raw，派生层才做规范化。
/// 无法转换的值留在原位置（Normalized 为 null）并附诊断，不为画图丢掉。
/// </summary>
public sealed record Quantity(double Value, string Unit, string? Datum, string Raw)
{
    private static readonly Dictionary<string, (string Canon, double Factor)> LengthUnits = new()
    {
        ["m"] = ("m", 1.0), ["cm"] = ("m", 0.01), ["mm"] = ("m", 0.001),
        ["mbsf"] = ("m", 1.0), ["ft"] = ("m", 0.3048),
    };

    private static readonly Dictionary<string, (string Canon, double Factor)> AgeUnits = new()
    {
        ["ka"] = ("ka", 1.0), ["Ma"] = ("ka", 1000.0),
        ["yrBP"] = ("ka", 0.001), ["aBP"] = ("ka", 0.001), ["calYrBP"] = ("ka", 0.001),
    };

    /// <summary>规范化到基准单位（深度→m@datum，年龄→ka）。失败返回 null 并给出诊断。</summary>
    public double? Normalize(out Diagnostic? diag)
    {
        var table = Datum is null ? AgeUnits : LengthUnits;
        var kind = Datum is null ? "age" : "depth";
        if (table.TryGetValue(Unit, out var conv))
        {
            diag = null;
            return Value * conv.Factor;
        }
        diag = new Diagnostic("unit.unconvertible",
            $"无法转换的{kind}单位 '{Unit}'（原始值 '{Raw}' 保留在原位置）", "quantity", "error");
        return null;
    }
}

/// <summary>深度基准（不可变材料）。</summary>
public sealed record DepthDatum(string Id, string Name, string ZeroAt, string PositiveDirection);

/// <summary>分段缺口/不整合等显式边界（不可变材料）。年龄单调性只在不断层段内成立。</summary>
public enum BoundaryKind { Gap, Unconformity, Hiatus }

public sealed record SegmentBoundary(string Id, Quantity Depth, BoundaryKind Kind, string Note);

/// <summary>岩芯分段（不可变材料）。</summary>
public sealed record CoreSegment(string Id, string CoreId, Quantity Top, Quantity Bottom, string Lithology);

/// <summary>放射性年龄分布：分布本体，绝不被均值替代。</summary>
[JsonPolymorphic(TypeDiscriminatorPropertyName = "kind")]
[JsonDerivedType(typeof(EmpiricalDistribution), "empirical")]
[JsonDerivedType(typeof(NormalDistribution), "normal")]
[JsonDerivedType(typeof(UniformDistribution), "uniform")]
[JsonDerivedType(typeof(LogNormalDistribution), "lognormal")]
public abstract record AgeDistribution
{
    /// <summary>确定性分位点（用于可重放的集合抽样，不用随机数）。</summary>
    public abstract double Quantile(double p);
    public abstract double Median();
    public abstract string Describe();
}

/// <summary>实测样本分布（含长尾）。</summary>
public sealed record EmpiricalDistribution(double[] Samples) : AgeDistribution
{
    public override double Quantile(double p)
    {
        var s = Samples.OrderBy(x => x).ToArray();
        if (s.Length == 0) return double.NaN;
        var idx = p * (s.Length - 1);
        var lo = (int)Math.Floor(idx); var hi = Math.Min(lo + 1, s.Length - 1);
        return s[lo] + (s[hi] - s[lo]) * (idx - lo);
    }
    public override double Median() => Quantile(0.5);
    public override string Describe() => $"empirical(n={Samples.Length})";
}

public sealed record NormalDistribution(double Mean, double Sigma) : AgeDistribution
{
    public override double Quantile(double p) => Mean + Sigma * InverseNormalCdf(p);
    public override double Median() => Mean;
    public override string Describe() => $"normal(μ={Mean},σ={Sigma})";
    internal static double InverseNormalCdf(double p)
    {
        // Acklam 近似，确定性。
        if (p <= 0) return -10; if (p >= 1) return 10;
        double[] a = { -3.969683028665376e+01, 2.209460984245205e+02, -2.759285104469687e+02, 1.383577518672690e+02, -3.066479806614716e+01, 2.506628277459239e+00 };
        double[] b = { -5.447609879822406e+01, 1.615858368580409e+02, -1.556989798598866e+02, 6.680131188771972e+01, -1.328068155288572e+01 };
        double[] c = { -7.784894002430293e-03, -3.223964580411365e-01, -2.400758277161838e+00, -2.549732539343734e+00, 4.374664141464968e+00, 2.938163982698783e+00 };
        double[] d = { 7.784695709041462e-03, 3.224671290700398e-01, 2.445134137142996e+00, 3.754408661907416e+00 };
        const double plow = 0.02425, phigh = 1 - plow;
        if (p < plow) { var q = Math.Sqrt(-2 * Math.Log(p)); return (((((c[0] * q + c[1]) * q + c[2]) * q + c[3]) * q + c[4]) * q + c[5]) / ((((d[0] * q + d[1]) * q + d[2]) * q + d[3]) * q + 1); }
        if (p > phigh) { var q = Math.Sqrt(-2 * Math.Log(1 - p)); return -(((((c[0] * q + c[1]) * q + c[2]) * q + c[3]) * q + c[4]) * q + c[5]) / ((((d[0] * q + d[1]) * q + d[2]) * q + d[3]) * q + 1); }
        var qq = p - 0.5; var r = qq * qq;
        return (((((a[0] * r + a[1]) * r + a[2]) * r + a[3]) * r + a[4]) * r + a[5]) * qq / (((((b[0] * r + b[1]) * r + b[2]) * r + b[3]) * r + b[4]) * r + 1);
    }
}

public sealed record UniformDistribution(double Min, double Max) : AgeDistribution
{
    public override double Quantile(double p) => Min + (Max - Min) * p;
    public override double Median() => (Min + Max) / 2;
    public override string Describe() => $"uniform({Min},{Max})";
}

/// <summary>长尾分布（对数正态），用于检验“年龄分布长尾”情形。</summary>
public sealed record LogNormalDistribution(double Mu, double Sigma) : AgeDistribution
{
    public override double Quantile(double p) => Math.Exp(Mu + Sigma * NormalDistribution.InverseNormalCdf(p));
    public override double Median() => Math.Exp(Mu);
    public override string Describe() => $"lognormal(μ={Mu},σ={Sigma})";
}

/// <summary>储层偏移（不可变材料，版本化参数引用）。</summary>
public sealed record ReservoirOffset(string Id, double OffsetKa, double SigmaKa, string Basis);

/// <summary>年龄锚点（不可变材料）：深度 + 完整分布 + 可选储层偏移。</summary>
public sealed record AgeAnchor(
    string Id, Quantity Depth, AgeDistribution Distribution,
    string? ReservoirOffsetId, string Method, string LabCode);

/// <summary>事件层与相关（不可变材料）。</summary>
public sealed record EventLayer(string Id, Quantity Depth, string Name, string? CorrelatesTo, double? CorrelatedAgeKa);

/// <summary>排除决定（可修改决定层，全部保留）。</summary>
public sealed record ExclusionDecision(string Id, string AnchorId, string Reason, string Reviewer, bool Rescinded = false);

/// <summary>参数版本（如储层偏移修订）。</summary>
public sealed record ParamVersion(int Version, string Note);

/// <summary>模型候选：多个等优模型需保留。</summary>
public sealed record ModelCandidate(
    string Id, string Label, string[] AcceptedAnchorIds, string[] ExcludedAnchorIds,
    double FitScore, bool Frozen, string Provenance);

/// <summary>深度-年龄曲线上的一个集合分位点。</summary>
public sealed record BandPoint(double DepthM, double P25, double P50, double P975);

public sealed record RateInterval(double FromDepthM, double ToDepthM, double MinCmPerKyr, double MaxCmPerKyr);

/// <summary>分段计算结果（局部重算的缓存单元）。</summary>
public sealed record SegmentResult(
    string SegmentKey, string InputDigest, BandPoint[] Band, RateInterval[] Rates,
    bool MonotonicOk, Diagnostic[] Diagnostics);

/// <summary>计算预算。</summary>
public sealed record Budget(int MaxEnsembleDraws, int MaxGridPoints, int MaxCandidates);

/// <summary>失败分类：资源预算耗尽 / 格式损坏 / 语义无解，是三种不同结果。</summary>
public abstract record ComputeOutcome
{
    public sealed record Ok(ModelCandidate[] Candidates, SegmentResult[] Segments, Diagnostic[] Diagnostics) : ComputeOutcome;
    public sealed record BudgetExhausted(string Detail) : ComputeOutcome;
    public sealed record FormatCorrupt(string Detail, string Path) : ComputeOutcome;
    public sealed record Unsolvable(Diagnostic[] Diagnostics) : ComputeOutcome;
}

/// <summary>冻结结果：成功或并发冲突（结构化，不是日志）。</summary>
public abstract record FreezeOutcome
{
    public sealed record Frozen(string ModelId, long EventSeq) : FreezeOutcome;
    public sealed record Conflict(string ModelId, long ExistingSeq, string Detail) : FreezeOutcome;
    public sealed record NotFound(string ModelId) : FreezeOutcome;
}

/// <summary>机器可读取事件：判断依据落库，可重放。</summary>
public sealed record ChronicleEvent(
    long Seq, string Id, string Kind, string PayloadJson,
    string InputDigest, int ParamVersion, string[] Parents, string CreatedAt);

public static class Digests
{
    public static string Of(string content)
    {
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(content));
        return Convert.ToHexString(hash)[..16].ToLowerInvariant();
    }

    public static string OfJson<T>(T value)
    {
        return Of(JsonSerializer.Serialize(value, Json.Options));
    }
}

public static class Json
{
    public static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = false,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Converters = { new JsonStringEnumConverter() },
    };
}
