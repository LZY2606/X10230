namespace ChronoCore;

/// <summary>离线案例包：领域材料（非通用表格）+ 叙事化操作步骤 + 反例情景定义。</summary>
public sealed record CaseBundle(
    string CaseId,
    string Title,
    string Domain,
    string Narrative,
    CaseBaseline Baseline,
    IReadOnlyList<CaseStep> Steps,
    IReadOnlyList<ReplayScenario> ReplayScenarios,
    int BundleFormatVersion);

public sealed record CaseBaseline(
    IReadOnlyList<DepthDatum> Datums,
    IReadOnlyList<CoreInterval> Intervals,
    IReadOnlyList<GapBoundary> Gaps,
    IReadOnlyList<Anchor> Anchors,
    IReadOnlyList<ReservoirOffset> Reservoirs,
    IReadOnlyList<EventLayer> EventLayers,
    IReadOnlyList<Exclusion> Exclusions,
    IReadOnlyList<ParameterSet> Parameters,
    string BaselineDigestNote);

public sealed record CaseStep(
    string StepId,
    string Phase,        // revise-parameter | local-source-change | conflicting-decision | review
    string Title,
    string Description,
    string? ParameterVersion,
    string? ReviseMaterialId,
    string? NewRawJson,
    string? DecisionId,
    string? DecisionKind,
    string? DecisionModelRef,
    string? DecisionReason,
    string? LocalScopeIntervalId = null);

public sealed record ReplayScenario(
    string Scenario,
    string Title,
    string PropositionCode,
    string InputFragmentJson,
    string Kind,           // add-anchor | revise-gap | revise-interval | parameter | concurrent-freeze
    string ExpectOutcome,  // semantic-impossible | success-contained | budget-exhausted
    string ExpectScope,
    string ExpectDetail);
