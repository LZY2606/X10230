using System.Text.Json.Serialization;

namespace ChronoCore;

/// <summary>
/// 所有变化都是不可变事件。事件只追加、不就地修改：
/// 材料修订通过 SourceDataChanged 发出新版本；排除决定通过 ExclusionRevoked 撤销。
/// 回到任意旧事件时按事件流重放恢复当时依赖版本与结论。
/// </summary>
[JsonPolymorphic(TypeDiscriminatorPropertyName = "t")]
[JsonDerivedType(typeof(ProjectCreated), "project-created")]
[JsonDerivedType(typeof(MaterialsImported), "materials-imported")]
[JsonDerivedType(typeof(SourceDataChanged), "source-data-changed")]
[JsonDerivedType(typeof(AnchorAdded), "anchor-added")]
[JsonDerivedType(typeof(ParametersRevised), "parameters-revised")]
[JsonDerivedType(typeof(ExclusionDecided), "exclusion-decided")]
[JsonDerivedType(typeof(ExclusionRevoked), "exclusion-revoked")]
[JsonDerivedType(typeof(BranchCreated), "branch-created")]
[JsonDerivedType(typeof(ModelDerived), "model-derived")]
[JsonDerivedType(typeof(ModelFrozen), "model-frozen")]
[JsonDerivedType(typeof(RuleJudged), "rule-judged")]
[JsonDerivedType(typeof(ReplayMarker), "replay-marker")]
public abstract record ProjectEvent
{
    public string EventType => GetType().Name;
}

public sealed record ProjectCreated(string ProjectId, string Name, string CaseVersion) : ProjectEvent;

/// <summary>不可变材料层导入：深度基准、缺口、锚点分布、储层偏移、事件层相关、排除历史。</summary>
public sealed record MaterialsImported(
    IReadOnlyList<DepthDatum> Datums,
    IReadOnlyList<DepthGap> Gaps,
    IReadOnlyList<Inversion> Inversions,
    IReadOnlyList<DepthAnchor> Anchors,
    IReadOnlyList<EventBed> EventBeds,
    IReadOnlyList<Exclusion> Exclusions,
    string SourceDigest) : ProjectEvent;

/// <summary>
/// 源数据修订：保留旧片段(FragmentBefore)与新片段(FragmentAfter)，
/// scope=local 时只允许影响引用同一材料的下游结果（局部重算）。
/// </summary>
public sealed record SourceDataChanged(
    string TargetMaterialId, string Field, string? FragmentBefore, string FragmentAfter,
    string Scope, string Reason) : ProjectEvent;

public sealed record AnchorAdded(DepthAnchor Anchor, string? TriggerScenario) : ProjectEvent;

public sealed record ParametersRevised(ModelParameters Parameters) : ProjectEvent;

public sealed record ExclusionDecided(Exclusion Exclusion) : ProjectEvent;
public sealed record ExclusionRevoked(string ExclusionId, string DecidedBy) : ProjectEvent;

/// <summary>从某个父事件分叉出新分支（冲突人工决定、第二分支）。</summary>
public sealed record BranchCreated(string NewBranchId, string ParentBranchId, long ParentSeq,
    string Reason, string DecidedBy) : ProjectEvent;

/// <summary>一次模型推导：记录输入摘要、参数版本、父级依赖、候选集合。</summary>
public sealed record ModelDerived(
    string DerivationId, string InputDigest, int ParametersVersion,
    IReadOnlyList<string> ParentEventIds, IReadOnlyList<string> CandidateModelIds,
    bool BudgetExhausted, IReadOnlyList<Diagnostic> Diagnostics) : ProjectEvent;

/// <summary>冻结某个候选模型；冻结后在该分支上对同一冻结目标的并发修改必须冲突。</summary>
public sealed record ModelFrozen(string DerivationId, string CandidateModelId, string Label) : ProjectEvent;

/// <summary>
/// 机器可读的规则判定。关键规则：
/// - monotonicity-within-contiguous-segment 年龄只能在不断层段中单调
/// - gaps-and-unconformities-are-boundaries 深度缺口与不整合是显式边界
/// - distribution-not-replaced-by-mean 一个锚点的分布不能被均值替代
/// - keep-all-equally-good-models 多个等优模型需保留
/// </summary>
public sealed record RuleJudged(
    string RuleId, bool Holds, string Scenario,
    IReadOnlyList<string> AffectedObjectIds,
    string Basis, string InputFragment) : ProjectEvent;

public sealed record ReplayMarker(string Label, string Scenario) : ProjectEvent;

/// <summary>事件信封：稳定序号、分支身份、父事件形成依赖图。</summary>
public sealed record EventRecord(
    long Seq,
    string EventId,
    string BranchId,
    string? ParentEventId,
    string Actor,
    DateTimeOffset AtUtc,
    ProjectEvent Payload)
{
    public string Type => Payload.EventType;
}
