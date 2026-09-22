using System.Text.Json.Serialization;

namespace ChronoAge.Core.CasePack;

/// <summary>
/// Offline domain case pack: "岩芯年代尺：深度锚点、沉积速率与多候选模型谱系".
/// It is a self-contained sedimentology narrative (not a generic spreadsheet): sections,
/// gaps/unconformities, radiometric distributions, reservoir offsets, event correlations and
/// retained exclusion decisions, plus a replayable script that walks the narrative through
/// boundary combinations and conflicting decisions.
/// </summary>
public sealed record CasePack(
    string Id,
    string Version,
    string Title,
    string Domain,
    string Narrative,
    IReadOnlyList<CaseMaterial> Materials,
    IReadOnlyList<CaseExclusion> Exclusions,
    IReadOnlyList<CaseStep> Steps)
{
    public const string PackId = "core-chronoruler";
    public const string PackVersion = "1.0.0";
}

/// <summary>
/// Raw import row. Every field is a string so the original representation (units, datum
/// tokens, unparsable values) reaches the import layer unchanged; normalization is derived.
/// </summary>
public sealed record CaseMaterial(
    string LogId,
    string Kind,
    string Source,
    IReadOnlyDictionary<string, string?> Fields);

public sealed record CaseExclusion(string MaterialLogId, bool Excluded, string Reason);

[JsonPolymorphic(TypeDiscriminatorPropertyName = "action")]
[JsonDerivedType(typeof(StepImport), "import")]
[JsonDerivedType(typeof(StepDerive), "derive")]
[JsonDerivedType(typeof(StepReviseParams), "revise_params")]
[JsonDerivedType(typeof(StepReviseMaterial), "revise_material")]
[JsonDerivedType(typeof(StepDecideOverlap), "decide_overlap")]
[JsonDerivedType(typeof(StepDecideBoundary), "decide_boundary")]
[JsonDerivedType(typeof(StepDecideTie), "decide_tie")]
[JsonDerivedType(typeof(StepDecideExclusion), "decide_exclusion")]
[JsonDerivedType(typeof(StepAccept), "accept_candidate")]
[JsonDerivedType(typeof(StepReject), "reject_candidate")]
[JsonDerivedType(typeof(StepFreeze), "freeze")]
[JsonDerivedType(typeof(StepCompareProbe), "compare_probe")]
[JsonDerivedType(typeof(StepUnfreeze), "unfreeze")]
[JsonDerivedType(typeof(StepBranch), "branch")]
[JsonDerivedType(typeof(StepExpect), "expect")]
public abstract record CaseStep(string Label, string? Note);

public sealed record StepImport(string BatchId, string Source, IReadOnlyList<string> MaterialLogIds, string? Label = null, string? Note = null)
    : CaseStep(Label ?? $"import:{BatchId}", Note);
public sealed record StepDerive(string? Label = null, string? Note = null) : CaseStep(Label ?? "derive", Note);
public sealed record StepReviseParams(ModelParameters Parameters, string Reason, string? Label = null, string? Note = null)
    : CaseStep(Label ?? "revise_params", Note);
public sealed record StepReviseMaterial(string LogId, IReadOnlyDictionary<string, string?> Fields, string Reason, string? LocalScopeOnly, string? Label = null, string? Note = null)
    : CaseStep(Label ?? $"revise:{LogId}", Note);
public sealed record StepDecideOverlap(string BoundaryLogId, string Choice, string Reason, string? Label = null, string? Note = null)
    : CaseStep(Label ?? $"overlap:{BoundaryLogId}", Note);
public sealed record StepDecideBoundary(string BoundaryLogId, string Interpretation, string Reason, string? Label = null, string? Note = null)
    : CaseStep(Label ?? $"boundary:{BoundaryLogId}", Note);
public sealed record StepDecideTie(string AnchorA, string AnchorB, string Order, string Reason, string? Label = null, string? Note = null)
    : CaseStep(Label ?? $"tie:{AnchorA}/{AnchorB}", Note);
public sealed record StepDecideExclusion(string MaterialLogId, bool Excluded, string Reason, string? Label = null, string? Note = null)
    : CaseStep(Label ?? $"exclude:{MaterialLogId}", Note);
public sealed record StepAccept(string CandidateId, string Reason, string? Label = null, string? Note = null)
    : CaseStep(Label ?? "accept", Note);
public sealed record StepReject(string CandidateId, string Reason, string? Label = null, string? Note = null)
    : CaseStep(Label ?? "reject", Note);
public sealed record StepFreeze(string CandidateId, string? Label = null, string? Note = null)
    : CaseStep(Label ?? "freeze", Note);
public sealed record StepCompareProbe(IReadOnlyDictionary<string, string?> ProbeFields, string? Label = null, string? Note = null)
    : CaseStep(Label ?? "compare_probe", Note);
public sealed record StepUnfreeze(string FrozenModelId, string Reason, string? Label = null, string? Note = null)
    : CaseStep(Label ?? "unfreeze", Note);
public sealed record StepBranch(string BranchId, string? Label = null, string? Note = null)
    : CaseStep(Label ?? $"branch:{BranchId}", Note);

/// <summary>Expectation checked at replay time; failures are recorded, not thrown away.</summary>
public sealed record StepExpect(
    string Check,
    string? TargetId,
    string? EqualsValue,
    double? MinValue,
    double? MaxValue,
    int? MinCount,
    int? MaxCount,
    bool? Exists,
    string? Label = null,
    string? Note = null)
    : CaseStep(Label ?? $"expect:{Check}", Note);
