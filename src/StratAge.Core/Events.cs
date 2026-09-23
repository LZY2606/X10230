using System.Text.Json;
using System.Text.Json.Serialization;

namespace StratAge.Core;

/// <summary>
/// 结构化领域事件。事件只追加、不可变；每个派生事件记录输入摘要、参数版本、父级依赖。
/// 回放到任一事件即恢复当时的依赖版本与可见结论。
/// </summary>
public sealed record RecordedEvent
{
    public required string EventId { get; init; }
    public required int Sequence { get; init; }
    public required string ProjectId { get; init; }
    public required string Branch { get; init; }
    public required string Type { get; init; }
    public required DateTimeOffset OccurredAt { get; init; }
    public required string Actor { get; init; }
    /// <summary>父事件 id（派生关系/决定链）。</summary>
    public string? ParentEventId { get; init; }
    /// <summary>输入摘要（源材料 sha256 + 参与对象 id 列表的哈希）。</summary>
    public required string InputDigest { get; init; }
    /// <summary>使用的参数/规则版本。</summary>
    public required string ParameterVersion { get; init; }
    /// <summary>判定状态：accepted / rejected / diagnostic / boundary ...</summary>
    public required string Status { get; init; }
    /// <summary>受影响对象稳定 id。</summary>
    public required IReadOnlyList<string> AffectedObjectIds { get; init; }
    public required JsonElement Payload { get; init; }
}

public static class EventTypes
{
    public const string SourceImported = "source.imported";
    public const string ParameterRevision = "parameters.revised";
    public const string SourcePatched = "source.patched";          // 只影响局部依赖
    public const string ManualDecision = "decision.manual";        // 人工接受/否定/调整
    public const string CandidatesDerived = "model.candidates";
    public const string BoundaryProbe = "probe.boundary";          // 反例边界触发
    public const string ModelFrozen = "model.frozen";
    public const string CompareAnchor = "model.compare-anchor";
    public const string RollbackView = "view.rollback";
    public const string ExclusionRecorded = "decision.exclusion";
}

public static class EventStatus
{
    public const string Accepted = "accepted";
    public const string Rejected = "rejected";
    public const string Adopted = "adopted";
    public const string NotAdopted = "not-adopted";
    public const string Diagnostic = "diagnostic";
    public const string Boundary = "boundary";
    public const string Conflict = "conflict";
    public const string Frozen = "frozen";
}

/// <summary>参数集：单调规则、跨缺口策略、枚举预算等，全部版本化。</summary>
public sealed record ParameterSet
{
    public required string Version { get; init; }
    public required string Label { get; init; }
    /// <summary>是否强制连续段内年龄随深度单调（向下变老）。</summary>
    public bool EnforceMonotonicWithinContinuous { get; init; } = true;
    /// <summary>缺口/不整合是显式边界：绝不跨边界插值或做单调判断。</summary>
    public bool TreatGapsAsExplicitBoundaries { get; init; } = true;
    /// <summary>长尾分布抽样次数（预算）。</summary>
    public int DistributionSamples { get; init; } = 400;
    /// <summary>候选枚举上限（资源预算）。</summary>
    public int MaxCandidates { get; init; } = 64;
    /// <summary>并列判定容差（yr）。</summary>
    public double TieToleranceYr { get; init; } = 0.5;
    public string? ParentVersion { get; init; }
    public string? Note { get; init; }
}
