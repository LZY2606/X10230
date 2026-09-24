using System.Text.Json;
using System.Text.Json.Serialization;

namespace AgeDepth.Domain;

/// <summary>不可变材料的种类。</summary>
public enum MaterialKind
{
    DepthDatum,              // 深度基准
    SegmentGap,              // 分段缺口（显式边界）
    Unconformity,            // 不整合（显式边界）
    RadiometricAge,          // 放射性年龄分布（锚点）
    ReservoirOffset,         // 储层偏移
    EventLayerCorrelation,   // 事件层相关
    ExclusionDecision        // 排除决定（作为材料永久保留）
}

/// <summary>不可变材料。导入后不可修改，只能被新决定引用或排除。</summary>
public sealed record Material
{
    public required string Id { get; init; }          // 稳定身份，导出/导入后保持
    public required MaterialKind Kind { get; init; }
    public required JsonElement Payload { get; init; } // 原始负载（保留原始表示）
    public required long Seq { get; init; }            // 导入顺序，保证等价输入得到稳定顺序
    public required string Hash { get; init; }         // 内容摘要
}

public sealed record DepthDatumPoint(double Depth, string Label);
public sealed record DepthDatumPayload(string Datum, string Unit, IReadOnlyList<DepthDatumPoint> Points);

public sealed record BoundaryPayload(Quantity Depth, string Reason);

/// <summary>放射性年龄：分布样本必须保留，不能被均值替代。</summary>
public sealed record RadiometricAgePayload(
    string AnchorId,
    Quantity Depth,
    IReadOnlyList<double> Samples,   // 后验样本（calBP），长尾保留
    string DistributionKind,          // 例如 "posterior-samples"
    Quantity? ReservoirOffsetApplied);

public sealed record ReservoirOffsetPayload(Quantity Offset, string AppliesToSegment, string Source);

public sealed record EventLayerPayload(string EventName, Quantity Depth, string CorrelatedTo, double Confidence);

public sealed record ExclusionPayload(string TargetMaterialId, string Rationale, string DecidedBy, long DecidedAtSeq);
