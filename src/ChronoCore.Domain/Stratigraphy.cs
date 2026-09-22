using ChronoCore.Domain.Quantities;

namespace ChronoCore.Domain;

public enum BoundaryKind
{
    /// <summary>正常连续接触。</summary>
    Conformable = 0,
    /// <summary>深度缺口（缺芯/无回收），显式边界，两侧不插值。</summary>
    CoreGap = 1,
    /// <summary>不整合：时间缺失的显式边界，单调性不得跨越。</summary>
    Unconformity = 2,
    /// <summary>倒置层段：构造倒转，深度增加年龄反而变轻（候选分段方向=-1）。</summary>
    Inverted = 3
}

/// <summary>岩芯分段（不断层段：年龄在段内随深度单调）。</summary>
public sealed class Segment
{
    public required string Id { get; init; }
    public required string CoreId { get; init; }
    public Depth Top { get; init; } = null!;
    public Depth Bottom { get; init; } = null!;
    public string Lithology { get; init; } = "";
    public string SourceMaterialId { get; init; } = "";
    /// <summary>段内极性：+1 正常（下老上新深），-1 倒置。</summary>
    public int Polarity { get; set; } = 1;
    public bool Converted => Top.Converted && Bottom.Converted;
    public IEnumerable<string> Diagnostics()
    {
        if (!Top.Converted) yield return $"分段 {Id} 顶界深度无法转换：{Top.RawText}";
        if (!Bottom.Converted) yield return $"分段 {Id} 底界深度无法转换：{Bottom.RawText}";
    }
}

/// <summary>分段之间的显式边界（缺口/不整合/倒置标记）。</summary>
public sealed record Boundary
{
    public required string Id { get; init; }
    public required string AboveSegmentId { get; init; }
    public required string BelowSegmentId { get; init; }
    public required BoundaryKind Kind { get; init; }
    public Depth Depth { get; init; } = null!;
    /// <summary>不整合处缺失时间的估计（年），未知为 null（不用默认值伪装）。</summary>
    public double? MissingYears { get; init; }
    public string Note { get; init; } = "";
    public required string SourceMaterialId { get; init; }
}

public enum AnchorKind { Radiocarbon = 0, ArAr = 1, Tephra = 2, Biostrat = 3, Magneto = 4 }

/// <summary>年龄锚点：深度 + 年龄分布（绝不能被均值替代）。</summary>
public sealed class Anchor
{
    public required string Id { get; init; }
    public required string Label { get; init; }
    public AnchorKind Kind { get; init; }
    public Depth Depth { get; init; } = null!;
    public AgeDistribution Distribution { get; init; } = null!;
    public string? ReservoirId { get; init; }
    public AgeDistribution? CorrectedDistribution { get; set; }
    public string? SegmentId { get; set; }
    public required string SourceMaterialId { get; init; }
    public string LabCode { get; init; } = "";
    public string? TieGroupId { get; set; }
    public bool Excluded { get; set; }
    public string? ExclusionReason { get; set; }
    public string? ExclusionEventId { get; set; }
    public string? Diagnostic { get; set; }
}

/// <summary>事件层（火山灰/浊积等）：跨孔或跨段相关。</summary>
public sealed class EventLayer
{
    public required string Id { get; init; }
    public required string Label { get; init; }
    public Depth Depth { get; init; } = null!;
    /// <summary>相关键：相同键的事件层被视为同一时间面。</summary>
    public required string CorrelationKey { get; init; }
    public string? CorrelatedToEventId { get; init; }
    public double? TiedAgeYearsBp { get; init; }
    public required string SourceMaterialId { get; init; }
    public string Note { get; init; } = "";
}

/// <summary>排除决定：所有排除都保留（可追溯、可恢复）。</summary>
public sealed record ExclusionDecision
{
    public required string Id { get; init; }
    public required string TargetId { get; init; }
    public required string TargetKind { get; init; }
    public required string Reason { get; init; }
    public required string SourceMaterialId { get; init; }
    /// <summary>来源排除（材料自带）还是人工决定。</summary>
    public required bool Manual { get; init; }
    public DateTimeOffset DecidedAt { get; init; }
}

/// <summary>不可变原始材料：导入后永不修改，只追加引用它的决定事件。</summary>
public sealed class SourceMaterial
{
    public required string Id { get; init; }
    public required string Title { get; init; }
    public required string Origin { get; init; }
    public required string RawText { get; init; }
    public DateTimeOffset ImportedAt { get; init; }
    public required string ImportEventId { get; init; }
}
