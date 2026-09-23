using System.Text.Json.Serialization;

namespace ChronoCore;

public sealed record ProjectCreatedPayload(string Name, string InitialBranchId, string? Note);

public sealed record MaterialImportedPayload(
    string Kind, // datum | interval | gap | anchor | reservoir | event
    string MaterialId,
    string? BaselineDigest,
    string? ChangedSourceId,
    string RawJson,            // 保留原始表示
    string? UnconvertibleNote);

public sealed record MaterialRevisedPayload(
    string Kind, string MaterialId, string PreviousEventId, string NewRawJson,
    string? BaselineDigest, string? ChangedSourceId, string Scope); // scope=local 时只影响局部依赖

public sealed record MaterialExcludedPayload(Exclusion Exclusion);

public sealed record ParameterRevisedPayload(ParameterSet Parameters, string PreviousVersion);

public sealed record DecisionPayload(Decision Decision, bool ConflictsWithPrior, string? PriorDecisionId);

public sealed record ModelsDerivedPayload(
    string InputDigest, string ParameterVersion,
    IReadOnlyList<string> ModelIds,
    IReadOnlyList<string> AdoptedModelIds,
    string OutcomeKind,
    IReadOnlyList<string> DiagnosticCodes,
    string? RecomputeScope,
    int BudgetRequested, int BudgetUsed);

public sealed record ModelFrozenPayload(string ModelId, string InputDigest, string ParameterVersion);
public sealed record FreezeConflictPayload(
    string ModelId, string HolderActor, string AttemptActor, string HeldSinceEventId, string Reason);

public sealed record ImpactPayload(
    string FrozenModelId, string TrialAnchorId, bool MonotonicPreserved,
    double? MaxAgeShiftCalA, IReadOnlyList<string> AffectedIntervalIds,
    IReadOnlyList<string> DiagnosticCodes, bool Adopted);

public sealed record BranchForkedPayload(
    string ParentBranchId, string NewBranchId, long FromSeq,
    string? BaselineDigest, string? ChangedSourceId, string Purpose);

/// <summary>“机器可读取的事件”判定依据：命题、适用范围、证据与裁决全部结构化。</summary>
public sealed record JudgmentPayload(
    string Scenario,                  // depth-duplicate|unconformity|inverted|tie-anchor|long-tail|local-recompute|concurrent-freeze
    string PropositionCode,           // MONO-IN-SECTION / EXPLICIT-BOUNDARY / DISTRIBUTION-NOT-MEAN / KEEP-EQUALLY-OPTIMAL
    bool Holds,
    string Determination,             // contained | semantic-impossible | budget-exhausted | retained | rejected
    string AffectedScopeJson,         // 受影响对象（结构化）
    IReadOnlyList<string> EvidenceEventIds,
    IReadOnlyList<string> InputFragmentIds,
    string? InputFragmentJson,        // 触发该情形的输入片段
    string? Detail);
