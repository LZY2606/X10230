using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace AgeDepthLab.Core;

/// <summary>年龄分布。一个锚点的分布不能被均值替代：始终保留完整形态。</summary>
public sealed record AgeDistribution(
    string Kind,                 // normal | uniform | samples
    Quantity? Mean,              // normal 用
    Quantity? Sigma,             // normal 用
    Quantity? Min,               // uniform 用
    Quantity? Max,               // uniform 用
    IReadOnlyList<double>? Samples, // samples 用（原始表示，单位见 SampleUnit），长尾原样保留
    string? SampleUnit,
    string SampleDatum)
{
    /// <summary>确定性抽样（种子可复现）。返回规范化 yr 值。</summary>
    public double Draw(Random rng, string subjectId, List<RunDiagnostic> diags)
    {
        switch (Kind)
        {
            case "normal":
            {
                var m = UnitConverter.Age(Mean!, subjectId);
                var s = UnitConverter.Age(Sigma!, subjectId);
                if (!m.Ok) { diags.Add(m.Diagnostic!); return double.NaN; }
                if (!s.Ok) { diags.Add(s.Diagnostic!); return double.NaN; }
                // Box-Muller
                var u1 = 1.0 - rng.NextDouble();
                var u2 = rng.NextDouble();
                return m.Normalized!.Value.Value + s.Normalized!.Value.Value *
                    Math.Sqrt(-2.0 * Math.Log(u1)) * Math.Cos(2.0 * Math.PI * u2);
            }
            case "uniform":
            {
                var lo = UnitConverter.Age(Min!, subjectId);
                var hi = UnitConverter.Age(Max!, subjectId);
                if (!lo.Ok) { diags.Add(lo.Diagnostic!); return double.NaN; }
                if (!hi.Ok) { diags.Add(hi.Diagnostic!); return double.NaN; }
                return lo.Normalized!.Value.Value +
                       rng.NextDouble() * (hi.Normalized!.Value.Value - lo.Normalized!.Value.Value);
            }
            case "samples":
            {
                var q = new Quantity(Samples![rng.Next(Samples.Count)], SampleUnit!, SampleDatum);
                var c = UnitConverter.Age(q, subjectId);
                if (!c.Ok) { diags.Add(c.Diagnostic!); return double.NaN; }
                return c.Normalized!.Value.Value;
            }
            default:
                diags.Add(new RunDiagnostic("DISTRIBUTION_UNKNOWN", "error",
                    $"未知分布类型 '{Kind}'", subjectId, null, null));
                return double.NaN;
        }
    }
}

// ---------- 不可变材料层 ----------

public sealed record DepthDatum(string Id, string Name, string Convention, double OriginOffsetM);

public sealed record CoreSegment(string Id, Quantity Top, Quantity Bottom, string Lithology);

/// <summary>显式边界：深度缺口(gap)与不整合(unconformity)。单调性不得跨越边界。</summary>
public sealed record SegmentBoundary(string Id, string Kind, Quantity At, string Note)
{
    public const string Gap = "gap";
    public const string Unconformity = "unconformity";
}

public sealed record AgeAnchor(
    string Id,
    Quantity Depth,
    AgeDistribution Distribution,
    Quantity? ReservoirOffset,  // 储层偏移（年从年龄中扣除），保留原始表示
    string Method,              // 如 14C / OSL / tephra
    string LabCode,
    string SourceRef);

public sealed record EventLayer(string Id, Quantity Depth, string CorrelatesTo, string Note);

// ---------- 可修改决定层（全部为事件，可重放） ----------

public sealed record ExclusionDecision(string TargetId, string Reason, string DecidedBy);

/// <summary>模型参数版本。任何修订产生新版本号，计算结果记录所用版本。</summary>
public sealed record ModelParameters(
    int Version,
    int DrawCount,        // 每个候选的集合抽样数
    int MaxDrawAttempts,  // 资源预算：尝试上限，超过即 BudgetExhausted
    int MinValidDraws,    // 低于此数即 SemanticUnsolvable
    double MonotonicToleranceYr,
    int Seed)
{
    public static ModelParameters Default => new(1, 240, 20000, 12, 0.0, 20260919);
}

/// <summary>不可变材料包：一次导入的全部材料，带内容哈希作为输入摘要。</summary>
public sealed record MaterialSet(
    DepthDatum Datum,
    IReadOnlyList<CoreSegment> Segments,
    IReadOnlyList<SegmentBoundary> Boundaries,
    IReadOnlyList<AgeAnchor> Anchors,
    IReadOnlyList<EventLayer> EventLayers)
{
    public string ContentHash()
    {
        var json = JsonSerializer.Serialize(this, JsonOptions.Default);
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(json)))[..16];
    }
}

public static class JsonOptions
{
    public static readonly JsonSerializerOptions Default = new()
    {
        WriteIndented = false,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };
}
