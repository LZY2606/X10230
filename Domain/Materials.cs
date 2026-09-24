namespace Chrono.Domain;

public enum MaterialKind
{
    DepthDatum = 0,      // 深度基准（mbsf / cm b.d. 等坐标基准）
    Segment = 1,         // 岩芯分段；isContinuous=false 即缺口/不整合边界
    AgeAnchor = 2,       // 放射性年龄锚点（携带完整分布）
    Reservoir = 3,       // 储层偏移（ΔR）
    EventBed = 4,        // 事件层（火山灰/同位素事件，用于跨岩芯相关）
    Exclusion = 5        // 排除决定（哪条材料被谁、依据哪版规则排除）
}

/// <summary>
/// 不可变材料：导入后永不修改。修改源数据 = 追加同 id 的新版本。
/// Verbatim 保留导入 JSON 原文片段；无法转换的值留在原位置并附诊断。
/// </summary>
public sealed class Material
{
    public required string Id { get; init; }              // 稳定身份，导出往返不变
    public required MaterialKind Kind { get; init; }
    public required int Version { get; init; }
    public required string SourceRef { get; init; }       // 直接来源（文件名/记录号）
    public required int SourceLine { get; init; }
    public required string Verbatim { get; init; }        // 原始片段原文
    public required string ImportedAt { get; init; }      // 仅供人看，不参与排序/身份
    public required string MaterialDigest { get; init; }  // 规范化内容摘要

    public RawQuantity? Depth { get; init; }
    public RawQuantity? DepthTop { get; init; }
    public RawQuantity? DepthBottom { get; init; }
    public string? SegmentId { get; init; }
    public bool? IsContinuous { get; init; }              // 分段：true 连续 / false 缺口或不整合
    public string? BoundaryType { get; init; }            // gap | unconformity | normal
    public bool? Inverted { get; init; }                  // 显式声明倒置层段（合法候选保留）
    public string? TieGroup { get; init; }                // 并列锚点组：同组等优
    public string? Label { get; init; }
    public string? Method { get; init; }

    public AgeDistribution? Distribution { get; init; }
    public ReservoirOffset? Reservoir { get; init; }

    // 事件层相关
    public string? EventCode { get; init; }
    public List<string> CorrelatesTo { get; init; } = new();

    // 排除决定
    public string? ExcludesMaterialId { get; init; }
    public string? Rationale { get; init; }
    public string? RuleVersion { get; init; }

    public List<string> Diagnostics { get; init; } = new();

    public string Key => $"{Id}#v{Version}";
}

/// <summary>材料被采纳/否决的结构化判定（机器可读事件引用）。</summary>
public enum MaterialStatus { Accepted = 0, Rejected = 1, Deferred = 2 }

public sealed record MaterialDecisionView(
    string MaterialId, int Version, MaterialStatus Status, string? Rationale, string RuleVersion);
