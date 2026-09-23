using System.Text.Json.Serialization;

namespace DepthAge.Domain;

/// <summary>
/// Offline case pack: "岩芯年代尺——深度锚点、沉积速率与多候选模型谱系".
/// A single narrative of material import -> parameter revision -> localized source change ->
/// conflicting decision, with explicit assertions for every boundary situation.
/// </summary>
public sealed record CasePack
{
    public required string CasePackId { get; init; }
    public required string Title { get; init; }
    public required string ProjectId { get; init; }
    public required string CoreId { get; init; }
    public required string CoreTitle { get; init; }
    public required string AgeDatum { get; init; }
    public required DatumRef Datum { get; init; }
    public required string Narrative { get; init; }
    public required CaseStep[] Steps { get; init; }
    public required string[] GlobalAsserts { get; init; }
}

public sealed record CaseStep
{
    public required string Id { get; init; }
    public required string Label { get; init; }
    public required string Branch { get; init; }
    public required CaseAction Action { get; init; }
    public string[] Asserts { get; init; } = [];
    public bool MarksBaseline { get; init; }
}

[JsonPolymorphic(TypeDiscriminatorPropertyName = "action")]
[JsonDerivedType(typeof(CreateProjectAction), "createProject")]
[JsonDerivedType(typeof(DeclareDatumAction), "declareDatum")]
[JsonDerivedType(typeof(ImportSegmentsAction), "importSegments")]
[JsonDerivedType(typeof(DeclareBoundaryAction), "declareBoundary")]
[JsonDerivedType(typeof(ImportAnchorsAction), "importAnchors")]
[JsonDerivedType(typeof(ImportEventBedsAction), "importEventBeds")]
[JsonDerivedType(typeof(ImportCorrelationsAction), "importCorrelations")]
[JsonDerivedType(typeof(ImportReservoirAction), "importReservoir")]
[JsonDerivedType(typeof(AssignReservoirAction), "assignReservoir")]
[JsonDerivedType(typeof(PublishRulesAction), "publishRules")]
[JsonDerivedType(typeof(ExcludeAnchorAction), "excludeAnchor")]
[JsonDerivedType(typeof(RevokeExclusionAction), "revokeExclusion")]
[JsonDerivedType(typeof(AssertPolicyAction), "assertPolicy")]
[JsonDerivedType(typeof(DeriveAction), "derive")]
[JsonDerivedType(typeof(FreezeAction), "freeze")]
[JsonDerivedType(typeof(FreezeStaleAction), "freezeStale")]
[JsonDerivedType(typeof(CreateTrialAction), "createTrial")]
[JsonDerivedType(typeof(ForkBranchAction), "forkBranch")]
[JsonDerivedType(typeof(DecideAction), "decide")]
[JsonDerivedType(typeof(RecordConflictAction), "recordConflict")]
public abstract record CaseAction;

public sealed record CreateProjectAction : CaseAction
{
    public required string Title { get; init; }
}

public sealed record DeclareDatumAction : CaseAction { }

public sealed record SegmentDraft
{
    public required string Id { get; init; }
    public required string Label { get; init; }
    public required string TopRaw { get; init; }
    public required string BottomRaw { get; init; }
    public required string Lithology { get; init; }
    public required string SourceRef { get; init; }
}

public sealed record ImportSegmentsAction : CaseAction
{
    public required SegmentDraft[] Segments { get; init; }
    public required string SourceRef { get; init; }
}

public sealed record DeclareBoundaryAction : CaseAction
{
    public required string Id { get; init; }
    public required string AboveSegmentId { get; init; }
    public required string BelowSegmentId { get; init; }
    public required BoundaryKind Kind { get; init; }
    public required string Note { get; init; }
    public required string SourceRef { get; init; }
}

public sealed record AnchorDraft
{
    public required string Id { get; init; }
    public required AnchorKind Kind { get; init; }
    public required string DepthRaw { get; init; }
    public required AgeDist Age { get; init; }
    public string? ReservoirModelId { get; init; }
    public string? CorrelationId { get; init; }
    public required string LabRef { get; init; }
    public required string SourceRef { get; init; }
}

public sealed record ImportAnchorsAction : CaseAction
{
    public required AnchorDraft[] Anchors { get; init; }
    public required string SourceRef { get; init; }
}

public sealed record EventBedDraft
{
    public required string Id { get; init; }
    public required string Label { get; init; }
    public required string DepthRaw { get; init; }
    public required string SourceRef { get; init; }
}

public sealed record ImportEventBedsAction : CaseAction
{
    public required EventBedDraft[] Beds { get; init; }
    public required string SourceRef { get; init; }
}

public sealed record CorrelationDraft
{
    public required string Id { get; init; }
    public required string EventBedId { get; init; }
    public required string RegionalEvent { get; init; }
    public required AgeDist Age { get; init; }
    public required string SourceRef { get; init; }
}

public sealed record ImportCorrelationsAction : CaseAction
{
    public required CorrelationDraft[] Correlations { get; init; }
    public required string SourceRef { get; init; }
}

public sealed record ReservoirDraft
{
    public required string Id { get; init; }
    public required AgeDist Offset { get; init; }
    public required string AppliesTo { get; init; }
    public required string SourceRef { get; init; }
    public string? SupersedesId { get; init; }
}

public sealed record ImportReservoirAction : CaseAction
{
    public required ReservoirDraft Model { get; init; }
}

public sealed record AssignReservoirAction : CaseAction
{
    public required string AnchorId { get; init; }
    public required string ReservoirModelId { get; init; }
    public required string Reason { get; init; }
}

public sealed record PublishRulesAction : CaseAction
{
    public required RuleSet RuleSet { get; init; }
    public required string Reason { get; init; }
}

public sealed record ExcludeAnchorAction : CaseAction
{
    public required string AnchorId { get; init; }
    public required string Reason { get; init; }
    public required string DecidedBy { get; init; }
}

public sealed record RevokeExclusionAction : CaseAction
{
    public required string ExclusionAnchorId { get; init; }
    public required string Reason { get; init; }
    public required string DecidedBy { get; init; }
}

public sealed record AssertPolicyAction : CaseAction
{
    public required string PolicyId { get; init; }
    public required string Statement { get; init; }
    public string[] EvidenceStepIds { get; init; } = [];
}

public sealed record DeriveAction : CaseAction
{
    public int? RealizationBudget { get; init; }
}

public sealed record FreezeAction : CaseAction
{
    public required string Label { get; init; }
    public required string FrozenBy { get; init; }
}

/// <summary>Deliberately stale freeze to exercise the concurrent-freeze optimistic check.</summary>
public sealed record FreezeStaleAction : CaseAction
{
    public required string Label { get; init; }
    public required long StaleExpectedSeq { get; init; }
    public required string FrozenBy { get; init; }
}

public sealed record CreateTrialAction : CaseAction
{
    public required string TrialBranchId { get; init; }
    public required AnchorDraft TrialAnchor { get; init; }
    public required string Reason { get; init; }
}

public sealed record ForkBranchAction : CaseAction
{
    public required string NewBranchId { get; init; }
    public required string Reason { get; init; }
}

public sealed record DecideAction : CaseAction
{
    public required string Decision { get; init; } // accept | reject
    public string? ModelKey { get; init; }
    public required string DecidedBy { get; init; }
    public required string Rationale { get; init; }
}

public sealed record RecordConflictAction : CaseAction
{
    public required string ConflictId { get; init; }
    public required string Subject { get; init; }
    public required string[] Sides { get; init; }
    public required string Resolution { get; init; }
    public required string DecidedBy { get; init; }
}
