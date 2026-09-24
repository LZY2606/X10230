using System.Text.Json.Serialization;

namespace CoreChron.Domain;

// 离线领域案例包：岩芯年代尺（深度锚点、沉积速率、多候选模型谱系）
public sealed record CasePack(
    [property: JsonPropertyName("casepack_id")] string CasePackId,
    [property: JsonPropertyName("title")] string Title,
    [property: JsonPropertyName("schema")] string Schema,
    [property: JsonPropertyName("project")] ProjectInfo Project,
    [property: JsonPropertyName("depth_data")] IReadOnlyList<DepthDatum> DepthData,
    [property: JsonPropertyName("boundaries")] IReadOnlyList<CoreBoundary> Boundaries,
    [property: JsonPropertyName("anchors")] IReadOnlyList<AgeAnchor> Anchors,
    [property: JsonPropertyName("reservoirs")] IReadOnlyList<ReservoirOffset> Reservoirs,
    [property: JsonPropertyName("events_beds")] IReadOnlyList<EventBed> EventsBeds,
    [property: JsonPropertyName("calibration_curve")] CalibrationCurve? CalibrationCurve,
    [property: JsonPropertyName("initial_parameters")] ChronologyParameters InitialParameters,
    [property: JsonPropertyName("initial_decisions")] IReadOnlyList<DecisionSpec> InitialDecisions,
    [property: JsonPropertyName("stages")] IReadOnlyList<ReplayStage> Stages);

public sealed record DecisionSpec(
    [property: JsonPropertyName("kind")] DecisionKind Kind,
    [property: JsonPropertyName("targets")] IReadOnlyList<string> Targets,
    [property: JsonPropertyName("rationale")] string Rationale,
    [property: JsonPropertyName("by")] string By);

public enum StageActionType
{
    ParameterRevision,   // 参数修订
    PatchSource,         // 只影响局部依赖的源数据改动
    AddAnchor,           // 新锚点（冻结后比较）
    ExcludeAnchor,       // 互相冲突的人工决定之一
    ReinstatedAnchor,
    ExcludeEvent,
    ReinstatedEvent,
    ChooseCandidates,
    RejectCandidates,
    Freeze,
    Unfreeze
}

public sealed record ReplayStage(
    [property: JsonPropertyName("id")] string Id,
    [property: JsonPropertyName("label")] string Label,
    [property: JsonPropertyName("narrative")] string Narrative,
    [property: JsonPropertyName("action")] StageActionType Action,
    [property: JsonPropertyName("parameters")] ChronologyParameters? Parameters,
    [property: JsonPropertyName("patch")] SourcePatch? Patch,
    [property: JsonPropertyName("anchor")] AgeAnchor? Anchor,
    [property: JsonPropertyName("decision")] DecisionSpec? Decision,
    [property: JsonPropertyName("candidate_ids")] IReadOnlyList<string>? CandidateIds,
    [property: JsonPropertyName("expect")] StageExpectations Expect);

// 源数据改动：只改一个直接来源（anchor|event|datum|boundary|reservoir），记录旧值
public sealed record SourcePatch(
    [property: JsonPropertyName("material_type")] string MaterialType,
    [property: JsonPropertyName("material_id")] string MaterialId,
    [property: JsonPropertyName("field")] string Field,
    [property: JsonPropertyName("old_raw")] string OldRaw,
    [property: JsonPropertyName("new_raw")] string NewRaw,
    [property: JsonPropertyName("reason")] string Reason);

public sealed record StageExpectations(
    [property: JsonPropertyName("diagnostic_codes")] IReadOnlyList<string>? DiagnosticCodes,
    [property: JsonPropertyName("recomputed_targets")] IReadOnlyList<string>? RecomputedTargets,
    [property: JsonPropertyName("unaffected_targets")] IReadOnlyList<string>? UnaffectedTargets,
    [property: JsonPropertyName("candidate_count_at_least")] int? CandidateCountAtLeast,
    [property: JsonPropertyName("status")] string? Status,
    [property: JsonPropertyName("note")] string? Note);

// 反例档案
public sealed record CounterExampleArchive(
    [property: JsonPropertyName("archive_id")] string ArchiveId,
    [property: JsonPropertyName("title")] string Title,
    [property: JsonPropertyName("created_utc")] DateTime CreatedUtc,
    [property: JsonPropertyName("baseline")] ArchiveBaseline,
    [property: JsonPropertyName("runs")] IReadOnlyList<ArchiveRun>);

public sealed record ArchiveBaseline(
    [property: JsonPropertyName("description")] string Description,
    [property: JsonPropertyName("depth_data")] IReadOnlyList<DepthDatum> DepthData,
    [property: JsonPropertyName("boundaries")] IReadOnlyList<CoreBoundary> Boundaries,
    [property: JsonPropertyName("anchors")] IReadOnlyList<AgeAnchor> Anchors,
    [property: JsonPropertyName("reservoirs")] IReadOnlyList<ReservoirOffset> Reservoirs,
    [property: JsonPropertyName("events_beds")] IReadOnlyList<EventBed> EventsBeds,
    [property: JsonPropertyName("decisions")] IReadOnlyList<DecisionRecord> Decisions,
    [property: JsonPropertyName("parameters")] ChronologyParameters Parameters,
    [property: JsonPropertyName("result")] ChronologyResult Result,
    [property: JsonPropertyName("input_digest")] string InputDigest);

public enum ScenarioKind
{
    DuplicateDepth, Unconformity, InvertedSection, TiedAnchors,
    LongTailDistribution, LocalRecompute, ConcurrentFreeze
}

public sealed record ArchiveRun(
    [property: JsonPropertyName("id")] string Id,
    [property: JsonPropertyName("scenario")] ScenarioKind Scenario,
    [property: JsonPropertyName("single_source_change")] string SingleSourceChange,
    [property: JsonPropertyName("input_fragment")] string InputFragment,
    [property: JsonPropertyName("rule_events")] IReadOnlyList<RuleJudgementEvent> RuleEvents,
    [property: JsonPropertyName("affected_targets")] IReadOnlyList<string> AffectedTargets,
    [property: JsonPropertyName("unaffected_targets")] IReadOnlyList<string> UnaffectedTargets,
    [property: JsonPropertyName("adopted")] ChronologyResult? Adopted,
    [property: JsonPropertyName("rejected")] ChronologyResult? Rejected,
    [property: JsonPropertyName("scope_ok")] bool ScopeOk,
    [property: JsonPropertyName("note")] string? Note);

// 机器可读判据事件
public sealed record RuleJudgementEvent(
    [property: JsonPropertyName("id")] string Id,
    [property: JsonPropertyName("rule_id")] string RuleId,
    [property: JsonPropertyName("judgement")] string Judgement, // holds | violated | bounded | retained
    [property: JsonPropertyName("target_ids")] IReadOnlyList<string> TargetIds,
    [property: JsonPropertyName("evidence")] string Evidence,
    [property: JsonPropertyName("created_utc")] DateTime CreatedUtc);
