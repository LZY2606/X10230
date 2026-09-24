using System.Text.Json;
using System.Text.Json.Serialization;

namespace AgeModel;

// ---------- 值单位与坐标基准 ----------
public record Quantity(double Value, string Unit, string Datum);

// ---------- 不可变材料层（append-only，新版本产生新 id 版本，旧版保留） ----------
public abstract record Material
{
    public required string Id { get; init; }        // 稳定身份，跨导出导入保持不变
    public required int Version { get; init; }      // 同源修订递增
    public required string Raw { get; init; }       // 导入时的原始表示，原样保留
    public string? Unit { get; init; }
    public string? Datum { get; init; }             // 坐标/时间基准
    public abstract string Kind { get; }
}

public sealed record DepthDatum : Material        // 深度基准
{
    public override string Kind => "depth-datum";
    public required string CoreId { get; init; }
    public required double DepthM { get; init; }
    public required string DatumType { get; init; } // top / marker / section-boundary
}

public sealed record SegmentGap : Material        // 分段缺口 / 不整合（显式边界）
{
    public override string Kind => "segment-gap";
    public required string CoreId { get; init; }
    public required double FromDepthM { get; init; }
    public required double ToDepthM { get; init; }
    public required string GapKind { get; init; }   // gap | unconformity
    public required string Reason { get; init; }
}

public sealed record RadioAge : Material          // 放射性年龄分布（完整分布，不得用均值替代）
{
    public override string Kind => "radio-age";
    public required string CoreId { get; init; }
    public required double DepthM { get; init; }
    public required string Method { get; init; }    // 14C / 210Pb / OSL ...
    public required double[] SamplesKa { get; init; } // 分布样本（ka BP），保留长尾
    public string? ReservoirOffsetId { get; init; }
}

public sealed record ReservoirOffset : Material   // 储层偏移
{
    public override string Kind => "reservoir-offset";
    public required string CoreId { get; init; }
    public required double OffsetYears { get; init; }
    public required string AppliesTo { get; init; }
}

public sealed record EventLayer : Material        // 事件层相关
{
    public override string Kind => "event-layer";
    public required string CoreId { get; init; }
    public required double DepthM { get; init; }
    public required string EventName { get; init; }
    public double? AgeKa { get; init; }
    public required string Correlation { get; init; }
}

public sealed record ExclusionRecord : Material   // 排除决定（作为材料保留，永不删除）
{
    public override string Kind => "exclusion";
    public required string TargetId { get; init; }
    public required string Reason { get; init; }
    public required string DecidedBy { get; init; }
}

// ---------- 诊断（带位置，不允许空值伪装成功） ----------
public sealed record Diagnostic(string Code, string Message, string? MaterialId, double? DepthM, string Severity);

// ---------- 结果类型：资源耗尽 / 格式损坏 / 语义无解 是三种不同结果 ----------
public enum OutcomeStatus { Ok, Diagnostics, BudgetExhausted, FormatCorrupt, SemanticUnsolvable, Conflict }

public sealed record Outcome<T>
{
    public OutcomeStatus Status { get; init; }
    public T? Value { get; init; }
    public List<Diagnostic> Diagnostics { get; init; } = new();
    public static Outcome<T> Ok(T v) => new() { Status = OutcomeStatus.Ok, Value = v };
    public static Outcome<T> Fail(OutcomeStatus s, params Diagnostic[] d) =>
        new() { Status = s, Diagnostics = d.ToList() };
}

// ---------- 推导出的模型候选（派生层，记录输入摘要/参数版本/父级依赖） ----------
public sealed record ModelCandidate
{
    public required string Id { get; init; }         // 稳定身份 model:{core}:{segment}:{idx}
    public required string CoreId { get; init; }
    public required int SegmentIndex { get; init; }
    public required double FromDepthM { get; init; }
    public required double ToDepthM { get; init; }
    public required int CandidateIndex { get; init; }
    public required List<AnchorPoint> Anchors { get; init; }
    public required double RateMinCmPerKa { get; init; }   // 速率区间
    public required double RateMaxCmPerKa { get; init; }
    public required List<BandPoint> UncertaintyBand { get; init; } // 不确定度带（分位数，非均值）
    public required string InputHash { get; init; }        // 输入摘要
    public required string ParamVersion { get; init; }     // 参数版本
    public required List<string> DependsOn { get; init; }  // 父级依赖（材料 id）
    public required long DerivedAtSeq { get; init; }
    public string Status { get; init; } = "active";        // active | superseded | frozen
    public long? FrozenAtSeq { get; init; }
    public int FreezeVersion { get; init; }              // 乐观并发令牌
}

public sealed record AnchorPoint(string RadioAgeId, double DepthM, double MedianKa, double P05Ka, double P95Ka);
public sealed record BandPoint(double DepthM, double P05Ka, double P50Ka, double P95Ka);

// ---------- 事件（机器可读取，判断依据落库） ----------
public sealed record AppEvent
{
    public required long Seq { get; init; }
    public required string Id { get; init; }
    public required string Kind { get; init; }
    public required JsonElement Payload { get; init; }
    public required string InputHash { get; init; }
    public required string ParamVersion { get; init; }
    public required List<string> Parents { get; init; }
    public required string CreatedAt { get; init; }
}

// ---------- 参数（可修改决定层，版本化） ----------
public sealed record ParamSet(string Version, double MonotonicToleranceKa, int MaxCandidates, int DerivationBudget)
{
    public static ParamSet V1 => new("p1", 0.05, 8, 10000);
}

public static class Hashing
{
    public static string Of(string s)
    {
        var bytes = System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(s));
        return Convert.ToHexString(bytes)[..16];
    }
    public static string OfJson<T>(T v) => Of(JsonSerializer.Serialize(v, Json.Options));
}

public static class Json
{
    public static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        WriteIndented = false
    };
    public static string Ser<T>(T v) => JsonSerializer.Serialize(v, Options);
    public static T? De<T>(string s) => JsonSerializer.Deserialize<T>(s, Options);
}
