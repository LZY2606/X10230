namespace Chrono.Core.Domain;

public enum BoundaryKind { Contact, Gap, Unconformity }

public sealed record CoreMeta
{
    public required string Id { get; init; }
    public required string Name { get; init; }
    public required string DepthDatum { get; init; }
    public required string DepthDirection { get; init; } // down-positive
    public Depth? TotalLength { get; init; }
    public string RawLine { get; init; } = "";
}

/// <summary>连续岩芯分段。年龄单调性只在单个连续分段内被要求。</summary>
public sealed record CoreSegment
{
    public required string Id { get; init; }
    public required Depth Top { get; init; }
    public required Depth Bottom { get; init; }
    public string Lithology { get; init; } = "";
    public string RawLine { get; init; } = "";
}

/// <summary>深度缺口/不整合：显式边界，单调性检查在此中断。</summary>
public sealed record SegmentBoundary
{
    public required string Id { get; init; }
    public required Depth Depth { get; init; }
    public required BoundaryKind Kind { get; init; }
    public string MissingIntervalRaw { get; init; } = "";
    public double MissingMeters { get; init; }
    public string Note { get; init; } = "";
    public string RawLine { get; init; } = "";
}

public enum AnchorStatus { Accepted, Excluded, Quarantined }

/// <summary>
/// 年龄锚点。保留原始测量表示；CalibratedDist 是派生层（经碳库校正）的完整分布。
/// </summary>
public sealed record AgeAnchor
{
    public required string Id { get; init; }
    public required Depth Depth { get; init; }
    public required AgeMeasure Measure { get; init; }
    public string ReservoirId { get; init; } = "";
    public string Material { get; init; } = "";
    public string EventLayerId { get; init; } = "";
    public AnchorStatus Status { get; init; } = AnchorStatus.Accepted;
    public string ExclusionReason { get; init; } = "";
    public AgeDist? CalibratedDist { get; init; }
    public string RawLine { get; init; } = "";
}

/// <summary>事件层（火山灰/古地磁/同位素事件）及其跨孔对比。</summary>
public sealed record EventLayer
{
    public required string Id { get; init; }
    public required Depth Depth { get; init; }
    public required string Name { get; init; }
    public string Correlation { get; init; } = "";
    public AgeDist? ReferenceAge { get; init; }
    public string ReferenceBasis { get; init; } = "";
    public string RawLine { get; init; } = "";
}

/// <summary>排除决定本身是不可变材料（决定不可静默删除，只能被后续决定推翻）。</summary>
public sealed record ExclusionDecision
{
    public required string Id { get; init; }
    public required string TargetKind { get; init; } // anchor|event|segment
    public required string TargetId { get; init; }
    public required bool Excluded { get; init; }
    public required string Reason { get; init; }
    public string SourceRef { get; init; } = "";
    public string RawLine { get; init; } = "";
}

public sealed record SourceDocument
{
    public required string Id { get; init; }
    public required string Format { get; init; } // cta|json
    public required string RawText { get; init; }
    public required string Sha256 { get; init; }
    public DateTimeOffset ImportedAt { get; init; }
}
