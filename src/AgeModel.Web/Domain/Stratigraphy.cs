using System.Text.Json.Serialization;

namespace AgeModel.Domain;

/// <summary>分段缺口/不整合：深度区间 + 类型；不整合是显式边界，跨它不做单调/插值。</summary>
public sealed record SegmentGap(
    string Id, Depth Top, Depth Bottom, GapKind Kind, string? Note,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] Qty? HiatusYears = null)
{
    public bool IsBarrier => Kind is GapKind.Unconformity or GapKind.CoreBreak;
}

/// <summary>事件层：火山灰/生物面等，可与锚点相关；相关关系显式保留。</summary>
public sealed record EventBed(
    string Id, Depth At, string Name, string? CorrelationKey,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] AgeDistribution? Age = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)] double? CorrelationWeight = null);

/// <summary>放射性/事件年龄锚点：深度、原始表示、分布（非均值）、储层偏移引用、排除状态。</summary>
public sealed record AgeAnchor
{
    public required string Id { get; init; }
    public required Depth At { get; init; }
    public required AgeKind Kind { get; init; }
    public string? EventBedId { get; init; }
    public string? ReservoirId { get; init; }
    /// <summary>实验室编号等原始表示。</summary>
    public string? LabCode { get; init; }
    public required AgeDistribution Distribution { get; init; }
    /// <summary>排除决定是不可变材料的一部分（保留，含理由与决定者）。</summary>
    public Exclusion? Exclusion { get; init; }
    public string? Note { get; init; }
    public bool Active => Exclusion is null;
}

public sealed record Exclusion(string Reason, string DecidedBy, string DecidedAt, string? EvidenceRef);

/// <summary>连续层段（连续 = 内部无 Barrier 缺口）。年龄只允许在不断层段内单调。</summary>
public sealed record ContiguousSection(string Id, Depth Top, Depth Bottom, IReadOnlyList<string> GapIdsInside)
{
    public bool ContainsDepth(Depth d) => d.Cm >= Top.Cm && d.Cm <= Bottom.Cm;
}

/// <summary>岩芯：基准 + 深度范围 + 显式缺口列表。</summary>
public sealed record CoreProfile(
    string Id, string Name, Depth Top, Depth Bottom,
    IReadOnlyList<SegmentGap> Gaps,
    IReadOnlyDictionary<string, string> UnitsMetadata);

/// <summary>规则版本：所有派生结果记录参数版本。</summary>
public sealed record RuleVersion(string Id, int Number, string Name, string Notes,
    double MonotonicityProbabilityThreshold, int CandidateBudget, int MonteCarloDraws, int RateGridPoints);

/// <summary>一次人工决定（可修改决定层）。</summary>
public sealed record Decision(string Id, string AtEventId, string Type, string PayloadJson, string DecidedBy, string DecidedAt, string? SupersedesDecisionId);

/// <summary>不可变材料快照（基线或单源改动）。来源证据整体。</summary>
public sealed record SourceMaterials
{
    public required string SourceId { get; init; }
    public required string Label { get; init; }
    public required CoreProfile Core { get; init; }
    public required IReadOnlyList<AgeAnchor> Anchors { get; init; }
    public required IReadOnlyList<EventBed> EventBeds { get; init; }
    public required IReadOnlyList<ReservoirOffset> Reservoirs { get; init; }
    public required AgeBasis Basis { get; init; }
    public string? ParentSourceId { get; init; }
    public string? ChangeDescription { get; init; }
}
