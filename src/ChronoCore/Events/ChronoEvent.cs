using System.Text.Json.Serialization;

namespace ChronoCore.Events;

/// <summary>追加写入的事件信封。身份稳定：eventId 一旦导出再导入保持不变。</summary>
public sealed class ChronoEvent
{
    public string EventId { get; set; }
    public string ProjectId { get; set; }
    public int Seq { get; set; }
    public string Branch { get; set; } = "main";
    public string Type { get; set; }
    public string PayloadJson { get; set; }
    public string InputSummary { get; set; }      // 输入摘要（含内容哈希）
    public string ParamVersion { get; set; }    // 参数版本
    public string ParentDepHash { get; set; }   // 父级依赖哈希
    public string Actor { get; set; }
    public string CreatedAt { get; set; }
    public bool Frozen { get; set; }            // 冻结分支事件：之后不可追加
}

/// <summary>事件载荷判别器。</summary>
[JsonPolymorphic(TypeDiscriminatorPropertyName = "kind")]
[JsonDerivedType(typeof(ProjectCreated), "project-created")]
[JsonDerivedType(typeof(MaterialImported), "material-imported")]
[JsonDerivedType(typeof(ParamSet), "param-set")]
[JsonDerivedType(typeof(DecisionRecorded), "decision")]
[JsonDerivedType(typeof(SourceDataChanged), "source-data-changed")]
[JsonDerivedType(typeof(ModelDerived), "model-derived")]
[JsonDerivedType(typeof(ModelAccepted), "model-accepted")]
[JsonDerivedType(typeof(BranchCreated), "branch-created")]
[JsonDerivedType(typeof(BranchFrozen), "branch-frozen")]
[JsonDerivedType(typeof(ConflictRecorded), "conflict")]
[JsonDerivedType(typeof(RuleJudgement), "rule-judgement")]
public abstract class EventPayload { }

public sealed class ProjectCreated : EventPayload
{
    public string Name { get; set; }
    public string CaseVersion { get; set; }
}

public sealed class MaterialImported : EventPayload
{
    public string MaterialKind { get; set; }   // datum|section|gap|age|reservoir|event|exclusion
    public string MaterialId { get; set; }
    public string Json { get; set; }
    public int SourceVersion { get; set; }
}

public sealed class ParamSet : EventPayload
{
    public string ParamVersion { get; set; }
    public string RuleSetVersion { get; set; }
    public double QuantileLow { get; set; } = 0.05;
    public double QuantileHigh { get; set; } = 0.95;
    public double MonotonicConfidence { get; set; } = 0.95;
    public int CandidateBudget { get; set; } = 256;
    public string Note { get; set; }
}

public sealed class DecisionRecorded : EventPayload
{
    public string DecisionId { get; set; }
    public string Kind { get; set; }           // accept-model|reject-model|exclude|include|parameter-adjust|correlation|manual
    public string TargetId { get; set; }
    public string Value { get; set; }
    public string Reason { get; set; }
    public string DecidedBy { get; set; }
}

/// <summary>直接来源改动：只允许影响声明的局部依赖范围。</summary>
public sealed class SourceDataChanged : EventPayload
{
    public string MaterialId { get; set; }
    public string MaterialKind { get; set; }
    public string OldJson { get; set; }
    public string NewJson { get; set; }
    public int NewSourceVersion { get; set; }
    public string Reason { get; set; }
    public string DirectDependencyScope { get; set; }  // local|global
}

public sealed class ModelDerived : EventPayload
{
    public string DerivationId { get; set; }
    public string ParamVersion { get; set; }
    public string InputHash { get; set; }
    public string Outcome { get; set; }        // derived|infeasible|budget-exhausted
    public string ReportJson { get; set; }
}

public sealed class ModelAccepted : EventPayload
{
    public string DerivationId { get; set; }
    public string CandidateId { get; set; }   // null = 否定全部 / 保留并列
    public bool Accepted { get; set; }
    public string Reason { get; set; }
}

public sealed class BranchCreated : EventPayload
{
    public string NewBranch { get; set; }
    public string FromEventId { get; set; }
}

public sealed class BranchFrozen : EventPayload
{
    public string FrozenBranch { get; set; }
    public string AtEventId { get; set; }
}

/// <summary>互相冲突的人工决定：结构化冲突状态，不静默覆盖。</summary>
public sealed class ConflictRecorded : EventPayload
{
    public string ConflictId { get; set; }
    public string FirstDecisionId { get; set; }
    public string SecondDecisionId { get; set; }
    public string Scope { get; set; }
    public string Resolution { get; set; }    // unresolved|first-wins|second-wins|both-rejected
}

/// <summary>
/// 机器可读的判断依据（关键业务规则）。
/// Rule: monotonic-within-section | gap-is-boundary | distribution-not-mean | ties-preserved | local-impact-only
/// </summary>
public sealed class RuleJudgement : EventPayload
{
    public string RuleId { get; set; }
    public string Status { get; set; }        // satisfied|violated|not-applicable
    public string SubjectId { get; set; }
    public string Detail { get; set; }
    public string EvidenceRefsCsv { get; set; }
}
