using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text;
using System.Security.Cryptography;

namespace AgeModel.Core;

public static class EventTypes
{
    public const string MaterialImported = "MaterialImported";
    public const string SourceAmended = "SourceAmended";
    public const string ParameterRevised = "ParameterRevised";
    public const string DecisionRecorded = "DecisionRecorded";
    public const string DerivationRecorded = "DerivationRecorded";
    public const string ModelFrozen = "ModelFrozen";
    public const string ModelUnfrozenRejected = "FreezeRejected";
    public const string BranchForked = "BranchForked";
    public const string ComparisonRecorded = "ComparisonRecorded";
    public const string RuleAssertion = "RuleAssertion";
}

/// <summary>追加式事件。Id/Hash 由内容与父哈希决定，导出再导入后身份不变。</summary>
public sealed record StoredEvent
{
    [JsonPropertyName("id")] public string Id { init; get; } = "";
    [JsonPropertyName("seq")] public long Seq { init; get; }
    [JsonPropertyName("type")] public string Type { init; get; } = "";
    [JsonPropertyName("branch")] public string Branch { init; get; } = "main";
    [JsonPropertyName("parentEventId")] public string? ParentEventId { init; get; }
    [JsonPropertyName("payload")] public JsonElement Payload { init; get; }
    [JsonPropertyName("actor")] public string Actor { init; get; } = "sed-group";
    [JsonPropertyName("occurredAt")] public string OccurredAt { init; get; } = "";
    [JsonPropertyName("inputSummary")] public string? InputSummary { init; get; }
    [JsonPropertyName("parameterVersion")] public string? ParameterVersion { init; get; }
    [JsonPropertyName("hash")] public string Hash { init; get; } = "";
    [JsonPropertyName("parentHash")] public string? ParentHash { init; get; }
}

public sealed class ConcurrencyConflictException : Exception
{
    public long ExpectedHeadSeq { get; }
    public long ActualHeadSeq { get; }
    public ConcurrencyConflictException(long expected, long actual)
        : base($"并发修改：期望头序号 {expected}，实际 {actual}")
    { ExpectedHeadSeq = expected; ActualHeadSeq = actual; }
}

public sealed class BranchFrozenException : Exception
{
    public string Branch { get; }
    public BranchFrozenException(string branch) : base($"分支 {branch} 的模型已冻结，写入需先派生新分支") => Branch = branch;
}

/// <summary>规则判断（机器可读）。供反例档案与 UI 复核直接消费。</summary>
public sealed record RuleAssertion(
    [property: JsonPropertyName("ruleId")] string RuleId,
    [property: JsonPropertyName("label")] string Label,
    [property: JsonPropertyName("holds")] bool Holds,
    [property: JsonPropertyName("severity")] string Severity,
    [property: JsonPropertyName("targetIds")] List<string> TargetIds,
    [property: JsonPropertyName("evidence")] string Evidence)
{
    public const string R1Monotonic = "R1-monotonic-within-continuous-segment";
    public const string R2GapBoundary = "R2-gap-and-unconformity-are-explicit-boundaries";
    public const string R3Distribution = "R3-anchor-distribution-not-replaced-by-mean";
    public const string R4TieRetention = "R4-tied-equivalent-models-retained";
}
