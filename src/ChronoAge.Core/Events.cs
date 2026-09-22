using System.Text.Json.Serialization;

namespace ChronoAge.Core;

/// <summary>
/// Append-only decision/evidence events. The journal is the source of truth; current state is
/// a replay. Rolling back to an old sequence restores the parameter versions and conclusions
/// visible then — later recomputation never overwrites history.
/// </summary>
public sealed record EventRecord(
    long Seq,
    string BranchId,
    string Kind,
    string Actor,
    string AtUtc,
    string PayloadJson,
    string? ParentSeq,
    string InputHash,
    string? Note)
{
    /// <summary>Stable identity of this event independent of its row position.</summary>
    public string EventId => $"EV{Seq:D6}";
}

[JsonPolymorphic(TypeDiscriminatorPropertyName = "kind")]
[JsonDerivedType(typeof(ProjectCreated), EventKinds.ProjectCreated)]
[JsonDerivedType(typeof(MaterialImported), EventKinds.MaterialImported)]
[JsonDerivedType(typeof(MaterialRevised), EventKinds.MaterialRevised)]
[JsonDerivedType(typeof(ParametersSet), EventKinds.ParametersSet)]
[JsonDerivedType(typeof(ModelDerived), EventKinds.ModelDerived)]
[JsonDerivedType(typeof(ExclusionDecided), EventKinds.Exclusion)]
[JsonDerivedType(typeof(OverlapResolved), EventKinds.OverlapResolved)]
[JsonDerivedType(typeof(BoundaryInterpreted), EventKinds.BoundaryInterpreted)]
[JsonDerivedType(typeof(TieResolved), EventKinds.TieResolved)]
[JsonDerivedType(typeof(CandidateAccepted), EventKinds.CandidateAccepted)]
[JsonDerivedType(typeof(CandidateRejected), EventKinds.CandidateRejected)]
[JsonDerivedType(typeof(ModelFrozen), EventKinds.ModelFrozen)]
[JsonDerivedType(typeof(ModelUnfrozen), EventKinds.ModelUnfrozen)]
[JsonDerivedType(typeof(NewAnchorCompared), EventKinds.NewAnchorCompared)]
[JsonDerivedType(typeof(ConflictRecorded), EventKinds.Conflict)]
public abstract record EventPayload;

public static class EventKinds
{
    public const string ProjectCreated = "project_created";
    public const string MaterialImported = "material_imported";
    public const string MaterialRevised = "material_revised";
    public const string ParametersSet = "parameters_set";
    public const string ModelDerived = "model_derived";
    public const string Exclusion = "exclusion_decided";
    public const string OverlapResolved = "overlap_resolved";
    public const string BoundaryInterpreted = "boundary_interpreted";
    public const string TieResolved = "tie_resolved";
    public const string CandidateAccepted = "candidate_accepted";
    public const string CandidateRejected = "candidate_rejected";
    public const string ModelFrozen = "model_frozen";
    public const string ModelUnfrozen = "model_unfrozen";
    public const string NewAnchorCompared = "new_anchor_compared";
    public const string Conflict = "conflict_recorded";

    public static readonly IReadOnlySet<string> DecisionKinds = new HashSet<string>
    {
        Exclusion, OverlapResolved, BoundaryInterpreted, TieResolved,
        CandidateAccepted, CandidateRejected, ModelFrozen, ModelUnfrozen
    };
}

public sealed record ProjectCreated(string ProjectId, string Name, string RuleVersion, string CasePackId, string CasePackVersion) : EventPayload;

/// <summary>A batch import of materials; raw fragments survive in the payload for replay.</summary>
public sealed record MaterialImported(
    string BatchId,
    string Source,
    IReadOnlyList<Material> Materials,
    IReadOnlyList<ImportDiagnostic> ImportDiagnostics) : EventPayload;

/// <summary>A revision of one direct source: new version rows, old rows marked superseded.</summary>
public sealed record MaterialRevised(
    string BatchId,
    string LogId,
    IReadOnlyList<Material> NewVersions,
    string Reason,
    string? LocalScopeOnly) : EventPayload;

public sealed record ImportDiagnostic(string Code, string Severity, string Message, string? ObjectId, string? Field, string? SourceFragment);

/// <summary>Versioned modelling parameters (a rule revision appends a new version event).</summary>
public sealed record ParametersSet(ModelParameters Parameters, string Reason) : EventPayload;

/// <summary>
/// Records a derivation run: input summary hash, parameter version, parent dependencies and
/// which material/decision/event ids the result depends on (for local recompute).
/// </summary>
public sealed record ModelDerived(
    string DerivationId,
    string ParametersVersion,
    string InputHash,
    IReadOnlyList<string> DependsOn,
    string Outcome,
    IReadOnlyList<string> CandidateIds,
    string? ArtifactId,
    int SamplesUsed,
    long BudgetSamples) : EventPayload;

public sealed record ExclusionDecided(string MaterialLogId, bool Excluded, string Reason, string? RuleVersion) : EventPayload;

/// <summary>Human call on overlapping/abutting sections: pick interpretation, possibly per boundary.</summary>
public sealed record OverlapResolved(string BoundaryLogId, string Choice, string Reason) : EventPayload;

/// <summary>A gap/unconformity may be kept as an explicit edge or interpreted; never silently interpolated.</summary>
public sealed record BoundaryInterpreted(string BoundaryLogId, string Interpretation, string Reason) : EventPayload;

/// <summary>Manual ordering for tied anchors (depth-equal). Any later conflicting decision is recorded.</summary>
public sealed record TieResolved(string AnchorA, string AnchorB, string Order, string Reason) : EventPayload;

public sealed record CandidateAccepted(string CandidateId, string Reason) : EventPayload;
public sealed record CandidateRejected(string CandidateId, string Reason) : EventPayload;

/// <summary>Freeze pins a candidate + its dependency set; later changes cannot mutate the frozen view.</summary>
public sealed record ModelFrozen(string FrozenModelId, string CandidateId, string DerivationId, IReadOnlyList<string> DependencyIds, string AtUtc) : EventPayload;
public sealed record ModelUnfrozen(string FrozenModelId, string Reason) : EventPayload;

/// <summary>Compare a hypothetical new anchor against the frozen model without accepting it.</summary>
public sealed record NewAnchorCompared(
    string FrozenModelId,
    DirectAnchor ProbeAnchor,
    string ComparisonId,
    IReadOnlyList<string> AffectedCandidateIds,
    bool MonotonicityHolds,
    IReadOnlyList<string> BreachSegmentIds) : EventPayload;

/// <summary>Two human decisions conflict (e.g. opposite overlap calls); both are retained.</summary>
public sealed record ConflictRecorded(string ConflictId, string EarlierEventId, string LaterEventId, string Scope, string Resolution) : EventPayload;
