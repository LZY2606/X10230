using System.Text.Json;
using System.Text.Json.Serialization;

namespace GeoChron.Core;

public enum EventType
{
    CaseImported,
    ParamsRevised,
    SourceDataChanged,
    DecisionRecorded,
    CandidatesDerived,
    ModelFrozen,
    NewAnchorCompared,
    ExperimentRecorded
}

/// <summary>
/// 不可变事件。回到任意旧事件时重放该序号之前的事件，恢复当时的依赖版本与可见结论，
/// 绝不拿最新计算结果覆盖历史。
/// </summary>
public sealed record EventRecord(
    [property: JsonPropertyName("seq")] long Seq,
    [property: JsonPropertyName("id")] string Id,
    [property: JsonPropertyName("caseId")] string CaseId,
    [property: JsonPropertyName("branch")] string Branch,
    [property: JsonPropertyName("type")] EventType Type,
    [property: JsonPropertyName("createdAt")] string CreatedAt,
    [property: JsonPropertyName("parentSeq")] long? ParentSeq,
    [property: JsonPropertyName("actor")] string Actor,
    [property: JsonPropertyName("label")] string Label,
    [property: JsonPropertyName("payload")] JsonElement Payload,
    [property: JsonPropertyName("inputSummary")] string? InputSummary,
    [property: JsonPropertyName("paramsVersion")] int? ParamsVersion,
    [property: JsonPropertyName("parentDependencies")] IReadOnlyList<string>? ParentDependencies,
    [property: JsonPropertyName("ruleVersion")] string RuleVersion);

public static class EventIds
{
    public static string New(EventType type, string canonicalPayload) =>
        $"{type.ToString().ToLowerInvariant()}-{StableIds.Hash(type + "|" + canonicalPayload)}";
}

/// <summary>反例档案：一次只改动一个直接来源的受控实验，采纳与否都保留。</summary>
public sealed record ExperimentRecord(
    [property: JsonPropertyName("id")] string Id,
    [property: JsonPropertyName("baselineInputSummary")] string BaselineInputSummary,
    [property: JsonPropertyName("perturbation")] string Perturbation,
    [property: JsonPropertyName("directSourceId")] string DirectSourceId,
    [property: JsonPropertyName("scenario")] string Scenario,
    [property: JsonPropertyName("expectedScope")] string ExpectedScope,
    [property: JsonPropertyName("observedOutcome")] string ObservedOutcome,
    [property: JsonPropertyName("affectedObjectIds")] IReadOnlyList<string> AffectedObjectIds,
    [property: JsonPropertyName("unaffectedObjectIds")] IReadOnlyList<string> UnaffectedObjectIds,
    [property: JsonPropertyName("scopeConfined")] bool ScopeConfined,
    [property: JsonPropertyName("adopted")] bool Adopted,
    [property: JsonPropertyName("adoptionNote")] string? AdoptionNote,
    [property: JsonPropertyName("judgments")] IReadOnlyList<MachineJudgment> Judgments);

/// <summary>机器可读的判断依据（规则代码 → 是否成立 → 证据对象）。</summary>
public sealed record MachineJudgment(
    [property: JsonPropertyName("ruleCode")] string RuleCode,
    [property: JsonPropertyName("holds")] bool Holds,
    [property: JsonPropertyName("evidence")] IReadOnlyList<string> Evidence,
    [property: JsonPropertyName("explanation")] string Explanation);

public static class RuleCodes
{
    public const string MonotoneWithinSection = "MONO-WITHIN-SECTION";
    public const string GapIsExplicitBoundary = "GAP-EXPLICIT-BOUNDARY";
    public const string DistributionNotMean = "DIST-NOT-MEAN";
    public const string KeepAllEqualOptima = "KEEP-EQUAL-OPTIMA";
    public const string LocalRecomputeScope = "LOCAL-RECOMPUTE-SCOPE";
    public const string FrozenModelImmutability = "FROZEN-IMMUTABLE";
    public const string StableIdentityRoundTrip = "STABLE-ID-ROUNDTRIP";
    public const string UnitsPreserved = "UNITS-PRESERVED";
}
