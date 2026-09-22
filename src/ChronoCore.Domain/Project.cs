using ChronoCore.Domain.Events;

namespace ChronoCore.Domain;

public enum DiagnosticSeverity { Info = 0, Warning = 1, Error = 2 }

/// <summary>带位置的诊断：输入不完整/无法转换时绝不以空值或默认值伪装成功。</summary>
public sealed record Diagnostic
{
    public required string Code { get; init; }
    public required DiagnosticSeverity Severity { get; init; }
    public required string Message { get; init; }
    /// <summary>位置：材料id + 字段路径（如 segments[2].bottom）。</summary>
    public required string Location { get; init; }
    public string? TargetId { get; init; }
    public string? ScenarioId { get; init; }
}

public sealed class Core
{
    public required string Id { get; init; }
    public required string Name { get; init; }
    public required Quantities.DepthDatum DepthDatum { get; init; }
    public double? RecoveryTopM { get; init; }
    public double? RecoveryBottomM { get; init; }
    public string Note { get; init; } = "";
}

/// <summary>项目可变状态：由事件流还原；不可变材料与可修改决定分层存放。</summary>
public sealed class ProjectState
{
    public required string Id { get; init; }
    public string Name { get; set; } = "";
    public DateTimeOffset CreatedAt { get; set; }
    public string CurrentRuleSetId { get; set; } = "";
    public Dictionary<string, RuleSet> RuleSets { get; } = new();
    public Dictionary<string, SourceMaterial> Materials { get; } = new();
    public Dictionary<string, Core> Cores { get; } = new();
    public Dictionary<string, Segment> Segments { get; } = new();
    public Dictionary<string, Boundary> Boundaries { get; } = new();
    public Dictionary<string, Quantities.ReservoirOffset> Reservoirs { get; } = new();
    public Dictionary<string, Anchor> Anchors { get; } = new();
    public Dictionary<string, EventLayer> EventLayers { get; } = new();
    public Dictionary<string, ExclusionDecision> Exclusions { get; } = new();
    /// <summary>冻结快照：候选在冻结时刻的不可变影像。</summary>
    public Dictionary<string, FrozenModel> Freezes { get; } = new();
    public long Version { get; set; }
}

/// <summary>冻结模型：不可变，冻结后再加入新锚点只产生对比投影，不改变冻结值。</summary>
public sealed record FrozenModel
{
    public required string Id { get; init; }
    public required string CandidateId { get; init; }
    public required string RuleSetId { get; init; }
    public required DateTimeOffset FrozenAt { get; init; }
    public required long EventSeq { get; init; }
    public required string InputDigest { get; init; }
    public required Derivation.CandidateModel Snapshot { get; init; }
    public required IReadOnlyList<string> IncludedAnchorIds { get; init; }
    public required string FreezeEventId { get; init; }
}
