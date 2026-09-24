using System.Text.Json;

namespace AgeDepth.Domain;

/// <summary>可修改决定层。决定可版本化、可取代，但历史决定全部保留。</summary>
public enum DecisionKind
{
    AcceptAnchor,        // 采纳锚点
    RejectAnchor,        // 否定锚点
    AdjustParameter,     // 参数修订（如储层偏移、抽样预算）
    FreezeModel,         // 冻结模型
    ExcludeAnchor,       // 排除锚点（镜像为 ExclusionDecision 材料）
    AdoptCandidate       // 采纳某候选为主模型
}

public sealed record Decision
{
    public required string Id { get; init; }
    public required DecisionKind Kind { get; init; }
    public required JsonElement Payload { get; init; }
    public required long Seq { get; init; }
    public string? Supersedes { get; init; }   // 取代的决定 id（历史仍保留）
    public required string RuleVersion { get; init; } // 作出决定时的规则版本
}

/// <summary>机器可读的判定事件：判断依据落为结构化事件，而非日志。</summary>
public sealed record DomainEvent
{
    public required string Id { get; init; }         // evt-000001 形式，稳定
    public required long Seq { get; init; }
    public required string Kind { get; init; }       // 例如 AnchorRejected / CandidateBranched / ModelFrozen
    public required JsonElement Payload { get; init; }
    public required IReadOnlyList<string> DependsOn { get; init; } // 依赖的材料/决定/事件 id
    public required string RuleVersion { get; init; }
    public required string SnapshotJson { get; init; } // 该事件时刻的可见结论快照
    public required string Hash { get; init; }
}
