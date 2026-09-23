namespace CoreChronology.Core;

public sealed record CasePackage(
    string SchemaVersion,
    string CaseId,
    string Title,
    string Narrative,
    ImmutableCaseMaterials Materials,
    InitialCaseRules Rules,
    IReadOnlyList<CaseStage> Stages,
    IReadOnlyList<CounterfactualDefinition> Counterfactuals,
    IReadOnlyList<JudgementDefinition> Judgements);

public sealed record ImmutableCaseMaterials(
    IReadOnlyList<DepthDatum> DepthDatums,
    IReadOnlyList<CoreSegment> Segments,
    IReadOnlyList<SegmentGap> Gaps,
    IReadOnlyList<RadiocarbonAge> Anchors,
    IReadOnlyList<ReservoirCorrection> Reservoirs,
    IReadOnlyList<EventLayer> EventLayers,
    IReadOnlyList<ExclusionDecision> Exclusions,
    IReadOnlyList<RawObservation> RawObservations);

public sealed record RawObservation(
    string Id,
    string Kind,
    string Location,
    string RawRepresentation,
    string IntendedUnit,
    string? IntendedDatum,
    string KeepInPlaceReason,
    IReadOnlyList<Diagnostic> Diagnostics);

public sealed record InitialCaseRules(
    ParameterVersion InitialParameters,
    IReadOnlyList<string> RequiredMachineJudgements);

public sealed record CaseStage(
    string Id,
    int Order,
    string Type,
    string Title,
    string Description,
    string? DirectSourceId,
    JsonPayload Payload);

public sealed record CounterfactualDefinition(
    string Id,
    string Label,
    string Trigger,
    string ChangedDirectSourceId,
    string ChangeDescription,
    string ExpectedScope,
    bool ShouldAdopt,
    string DecisionRationale,
    JsonPayload Change);

public sealed record JudgementDefinition(
    string Code,
    string TruthValue,
    string Basis,
    IReadOnlyList<string> EvidenceIds,
    string Scope);

public sealed record JsonPayload(string Json);
