using System.Text.Json.Serialization;

namespace Chrono.Domain;

public enum CommandOutcome
{
    /// <summary>接受并物化。</summary>
    Accepted,
    /// <summary>语义无解：结构合法但领域规则不允许（如跨不整合插值）。</summary>
    SemanticImpasse,
    /// <summary>资源预算耗尽（候选数/样本数超限）。</summary>
    BudgetExhausted,
    /// <summary>格式损坏：无法解析或引用完整性失败，事务回滚。</summary>
    FormatCorrupt
}

public enum EventType
{
    PackageImported,
    ParameterRevised,
    SourcePatched,
    AnchorExcluded,
    AnchorIncluded,
    ModelFrozen,
    ModelUnfrozen,
    AnchorCandidateAdded,
    ConflictDetected,
    ConflictResolved,
    ReviewAccepted,
    ReviewRejected,
    BranchForked,
    ReplayCheckpoint,
    ArchiveExported,
    ArchiveImported
}

/// <summary>
/// 追加式事件。Payload 为领域 JSON。PrevHash/Hash 构成仅追加哈希链；
/// 任何旧事件都不可被覆盖，回到旧事件即按当时依赖版本恢复。
/// </summary>
public sealed class LedgerEvent
{
    public long Seq { get; set; }
    public string EventId { get; set; } = "";
    public string BranchId { get; set; } = "";
    public EventType Type { get; set; }
    public string Actor { get; set; } = "";
    public string OccurredAt { get; set; } = "";
    public string PayloadJson { get; set; } = "";
    public string InputDigest { get; set; } = "";
    public string ParameterVersion { get; set; } = "";
    public string ParentEventId { get; set; } = "";
    public string PrevHash { get; set; } = "";
    public string Hash { get; set; } = "";
    public string Outcome { get; set; } = nameof(CommandOutcome.Accepted);
    public string? DiagnosticsJson { get; set; }
}

/// <summary>带位置的诊断。输入不完整时返回它，绝不用空值/默认值伪装成功。</summary>
public sealed record Diagnostic(
    string Code, string Severity, string Message,
    string? MaterialId, string? Field, string? Locator)
{
    public static Diagnostic Error(string code, string message, string? mat = null, string? field = null, string? loc = null)
        => new(code, "error", message, mat, field, loc);
    public static Diagnostic Warn(string code, string message, string? mat = null, string? field = null, string? loc = null)
        => new(code, "warning", message, mat, field, loc);
    public static Diagnostic Info(string code, string message, string? mat = null, string? field = null, string? loc = null)
        => new(code, "info", message, mat, field, loc);
}

/// <summary>可重放脚本的一步（案例包内的操作剧本）。</summary>
public sealed class ReplayStep
{
    public string StepId { get; set; } = "";
    public string Title { get; set; } = "";
    public string Narrative { get; set; } = "";
    public string Command { get; set; } = "";
    public JsonElementBody Args { get; set; } = new();
    public List<string> ExpectStates { get; set; } = new();
    public List<string> ExpectAffected { get; set; } = new();
}

/// <summary>JsonElement 的简单包装，序列化时直接透传。</summary>
public sealed class JsonElementBody
{
    [JsonExtensionData] public Dictionary<string, object?> Extra { get; set; } = new();
}
