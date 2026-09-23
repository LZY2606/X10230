using System.Text;
using System.Text.Json;

namespace DepthAge.Core.Models;

public enum EventType
{
    ProjectCreated,
    MaterialImported,     // datum/section/anchor/event/reservoir/calibration
    ExclusionDecided,
    ExclusionRevoked,
    RuleBookRevised,
    CandidateAccepted,
    CandidateRejected,
    ParameterAdjusted,
    ModelFrozen,
    Computed,
    NoteAdded
}

/// <summary>
/// 追加写事件。历史不可被新计算覆盖；旧事件恢复当时依赖版本与可见结论。
/// prevHash 形成哈希链，半写/损坏时按链恢复到最后一致点。
/// </summary>
public sealed record ProjectEvent
{
    public required long Seq { get; init; }
    public required string EventId { get; init; }
    public required EventType Type { get; init; }
    public required string PayloadJson { get; init; }
    public required string MaterialKind { get; init; } // Datum|Section|Anchor|...|Decision|RuleBook|Computation|Freeze|Project
    public required string SubjectId { get; init; }
    public required string? BranchId { get; init; }
    public required DateTimeOffset OccurredAt { get; init; }
    public required string Actor { get; init; }
    public required string InputDigest { get; init; }   // 该事件依赖输入摘要
    public required string? RuleVersion { get; init; }
    public required string? ParentDependencyId { get; init; } // 父级依赖（事件id/计算id）
    public required string PrevHash { get; init; }
    public required string Hash { get; init; }

    public const string GenesisHash = "GENESIS-SHA256-0";

    public string ComputeHash()
    {
        var canonical =
            $"{Seq}\n{EventId}\n{Type}\n{MaterialKind}\n{SubjectId}\n{BranchId}\n" +
            $"{OccurredAt:O}\n{Actor}\n{InputDigest}\n{RuleVersion}\n{ParentDependencyId}\n{PrevHash}\n{PayloadJson}";
        return Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(Encoding.UTF8.GetBytes(canonical)))
            .ToLowerInvariant();
    }

    public static ProjectEvent Create(
        long seq, EventType type, string materialKind, string subjectId,
        object payload, string inputDigest, string? ruleVersion, string? parentDependencyId,
        string prevHash, string? branchId = null, string actor = "sed-team", DateTimeOffset? occurredAt = null)
    {
        var payloadJson = JsonSerializer.Serialize(payload, EventStoreJson.Options);
        var provisional = new ProjectEvent
        {
            Seq = seq,
            EventId = StableId.ComputeRaw("evt", $"{seq}|{type}|{subjectId}|{payloadJson}"),
            Type = type,
            PayloadJson = payloadJson,
            MaterialKind = materialKind,
            SubjectId = subjectId,
            BranchId = branchId ?? "main",
            OccurredAt = occurredAt ?? DateTimeOffset.UtcNow,
            Actor = actor,
            InputDigest = inputDigest,
            RuleVersion = ruleVersion,
            ParentDependencyId = parentDependencyId,
            PrevHash = prevHash,
            Hash = ""
        };
        return provisional with { Hash = provisional.ComputeHash() };
    }

    public T? Payload<T>() => JsonSerializer.Deserialize<T>(PayloadJson, EventStoreJson.Options);
}

public static class EventStoreJson
{
    public static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = false,
        PropertyNameCaseInsensitive = true
    };
}
