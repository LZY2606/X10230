using System.Text.Json;
using System.Text.Json.Serialization;

namespace StratAge.Core;

/// <summary>深度基准：不可变材料。深度单位声明 + 向下为正的坐标约定。</summary>
public sealed record DepthDatum(
    string Id,
    string Name,
    string DepthPositive,           // "down"
    string DeclaredDepthUnit,       // 原始声明
    string? VerticalDatum,          // 例如 "core top (CT-0)"
    string? CoordinateSystem,       // 坐标系基准说明
    string? SourceRef);

public enum SegmentKind
{
    Continuous = 0,     // 连续岩芯段
    CoringGap = 1,      // 取芯缺口（缺材料，年龄不得跨段插值）
    Unconformity = 2,   // 不整合（显式时间边界）
    InvertedSection = 3,// 倒置层段（重建后标注）
}

/// <summary>岩芯分段：顶/底深度、段类型、是否允许跨边界单调约束。</summary>
public sealed record CoreSegment(
    string Id,
    int OrderIndex,
    Qty TopDepth,
    Qty BottomDepth,
    SegmentKind Kind,
    string Label,
    string? Note,
    string? SourceRef)
{
    public bool AllowsMonotonicAcross => Kind == SegmentKind.Continuous;
}

public enum DistributionShape { Point = 0, Normal = 1, StudentT = 2, Empirical = 3, Skewed = 4 }

/// <summary>
/// 放射性年龄的一次观测：原始表示（如 "4820 ± 35 14C yr BP"）原样保留；
/// 派生层规范化成分布描述。绝不用均值替代分布。
/// </summary>
public sealed record AgeObservation(
    string Method,             // AMS14C / Ar-Ar / varve-count ...
    string RawRepresentation,  // 实验室原文
    Qty Age,                   // 归一化后的中心值（可能为 null）
    double? PlusSigma,         // yr（规范化）
    double? MinusSigma,
    double? Df,                // Student-t 自由度（长尾）
    DistributionShape Shape,
    string? RawAgeUnit,
    IReadOnlyList<double> EmpiricalSamples, // 经验样本（如有），保留全部分位
    IReadOnlyList<Diag> Diagnostics);

/// <summary>储层偏移：原始 ΔR 表示 + 是否应用 + 关联锚点。</summary>
public sealed record ReservoirOffset(
    string Id,
    string TargetAnchorId,
    Qty DeltaR,
    Qty? DeltaRSigma,
    bool Applied,
    string? Basis,             // marine20 / local / ...
    string? SourceRef);

/// <summary>年龄锚点：深度 + 分布 + 与事件层的可能关联（可多个）。</summary>
public sealed record AgeAnchor(
    string Id,
    int OrderIndex,
    string MaterialCode,       // 植物碎屑/有孔虫/tephra...
    Qty Depth,
    AgeObservation Observation,
    string? ReservoirOffsetId,
    IReadOnlyList<string> ProposedEventIds,
    bool Excluded,
    string? ExclusionDecisionId,
    string? SourceRef);

public enum EventCorrelation { Proposed = 0, Adopted = 1, Rejected = 2 }

/// <summary>事件层（火山灰/古地磁等时面）：深度位置 + 关联决定。</summary>
public sealed record MarkerBed(
    string Id,
    int OrderIndex,
    string EventName,
    string EventType,          // tephra / paleomagnetic / ...
    Qty Depth,
    string? KnownAgeRaw,       // 已知事件年龄的原始表示
    Qty? KnownAge,
    EventCorrelation Correlation,
    string? DecidedAnchorId,
    string? SourceRef,
    string? Note);

/// <summary>排除决定：被排除对象（锚点/事件/段）+ 理由 + 决定来源，全部保留。</summary>
public sealed record ExclusionDecision(
    string Id,
    string TargetObjectId,
    string TargetKind,        // anchor / marker / segment
    string Reason,
    string DecidedBy,
    DateTimeOffset DecidedAt,
    string? SourceRef);

/// <summary>不可变源材料的一次导入（原始 JSON 也保留）。</summary>
public sealed record SourceBundle(
    string SourceId,
    string DirectOrigin,      // 唯一直接来源标识（实验室/野外记录）
    DateTimeOffset ImportedAt,
    string RawJson,
    string Sha256);
