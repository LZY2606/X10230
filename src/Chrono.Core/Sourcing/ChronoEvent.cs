namespace Chrono.Core.Sourcing;

/// <summary>
/// An append-only decision event. Immutable material changes and human decisions are the same log;
/// every event records the rule version, an input summary and the derivation ids it depended on, so
/// any historical state can be reconstructed without later computations overwriting it.
/// </summary>
public sealed record ChronoEvent(
    string Id,
    string ProjectId,
    string BranchId,
    long Seq,
    string Type,
    string PayloadJson,
    string RuleVersion,
    string InputSummary,
    IReadOnlyList<string> ParentDependencyIds,
    string? ForkFromEventId,
    string CreatedAt)
{
    public const string ProjectCreated = "project_created";
    public const string BaselineIngested = "baseline_ingested";
    public const string MaterialAmended = "material_amended";
    public const string ExclusionDecided = "exclusion_decided";
    public const string DuplicateResolved = "duplicate_resolved";
    public const string InversionBoundaryAdded = "inversion_boundary_added";
    public const string ParametersRevised = "parameters_revised";
    public const string CandidateDecided = "candidate_decided";
    public const string ModelFrozen = "model_frozen";
    public const string BranchForked = "branch_forked";
    public const string CompareAnchorAdded = "compare_anchor_added";
    public const string RuleFinding = "rule_finding";
}
