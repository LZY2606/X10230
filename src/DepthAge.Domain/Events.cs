using System.Text.Json.Serialization;

namespace DepthAge.Domain;

/// <summary>
/// Event-sourced history. Every import, decision, derivation, freeze and conflict is an
/// immutable event. Material events carry immutable payloads; decision events layer on top.
/// Replaying events up to any seq restores exactly the dependency versions and visible
/// conclusions of that moment.
/// </summary>
[JsonPolymorphic(TypeDiscriminatorPropertyName = "type")]
[JsonDerivedType(typeof(ProjectCreated), "projectCreated")]
[JsonDerivedType(typeof(DatumDeclared), "datumDeclared")]
[JsonDerivedType(typeof(SegmentsImported), "segmentsImported")]
[JsonDerivedType(typeof(BoundaryDeclared), "boundaryDeclared")]
[JsonDerivedType(typeof(AnchorsImported), "anchorsImported")]
[JsonDerivedType(typeof(EventBedsImported), "eventBedsImported")]
[JsonDerivedType(typeof(CorrelationsImported), "correlationsImported")]
[JsonDerivedType(typeof(ReservoirModelImported), "reservoirModelImported")]
[JsonDerivedType(typeof(ReservoirAssignmentChanged), "reservoirAssignmentChanged")]
[JsonDerivedType(typeof(RuleSetPublished), "ruleSetPublished")]
[JsonDerivedType(typeof(AnchorExcluded), "anchorExcluded")]
[JsonDerivedType(typeof(ExclusionRevoked), "exclusionRevoked")]
[JsonDerivedType(typeof(ModelsDerived), "modelsDerived")]
[JsonDerivedType(typeof(ModelFrozen), "modelFrozen")]
[JsonDerivedType(typeof(TrialBranchCreated), "trialBranchCreated")]
[JsonDerivedType(typeof(BranchForked), "branchForked")]
[JsonDerivedType(typeof(ModelAccepted), "modelAccepted")]
[JsonDerivedType(typeof(ModelRejected), "modelRejected")]
[JsonDerivedType(typeof(ConflictRecorded), "conflictRecorded")]
[JsonDerivedType(typeof(PolicyAsserted), "policyAsserted")]
public abstract record EventPayload;

public sealed record ProjectCreated : EventPayload
{
    public required string ProjectId { get; init; }
    public required string Title { get; init; }
    public required string CoreId { get; init; }
    public required string AgeDatum { get; init; }
}

public sealed record DatumDeclared : EventPayload
{
    public required DatumRef Datum { get; init; }
}

public sealed record SegmentsImported : EventPayload
{
    public required Segment[] Segments { get; init; }
    public required Diagnostic[] Diagnostics { get; init; }
    public required string SourceRef { get; init; }
}

public sealed record BoundaryDeclared : EventPayload
{
    public required BoundarySpec Boundary { get; init; }
}

public sealed record AnchorsImported : EventPayload
{
    public required Anchor[] Anchors { get; init; }
    public required Diagnostic[] Diagnostics { get; init; }
    public required string SourceRef { get; init; }
}

public sealed record EventBedsImported : EventPayload
{
    public required EventBed[] Beds { get; init; }
    public required Diagnostic[] Diagnostics { get; init; }
    public required string SourceRef { get; init; }
}

public sealed record CorrelationsImported : EventPayload
{
    public required EventCorrelation[] Correlations { get; init; }
    public required Diagnostic[] Diagnostics { get; init; }
    public required string SourceRef { get; init; }
}

public sealed record ReservoirModelImported : EventPayload
{
    public required ReservoirModel Model { get; init; }
    public required Diagnostic[] Diagnostics { get; init; }
}

/// <summary>Re-point an anchor at a different reservoir model version (decision layer).</summary>
public sealed record ReservoirAssignmentChanged : EventPayload
{
    public required string AnchorId { get; init; }
    public required string ReservoirModelId { get; init; }
    public required string Reason { get; init; }
}

public sealed record RuleSetPublished : EventPayload
{
    public required RuleSet RuleSet { get; init; }
    public required string Reason { get; init; }
}

public sealed record AnchorExcluded : EventPayload
{
    public required Exclusion Exclusion { get; init; }
}

public sealed record ExclusionRevoked : EventPayload
{
    public required string ExclusionId { get; init; }
    public required string Reason { get; init; }
    public required string DecidedBy { get; init; }
}

/// <summary>
/// A derivation run. The full result is content-addressed in the derivation store;
/// this event pins it into history so returning to this event restores these conclusions.
/// </summary>
public sealed record ModelsDerived : EventPayload
{
    public required string DerivationId { get; init; }
    public required string RuleSetVersion { get; init; }
    public required string InputsDigest { get; init; }
    public required string Status { get; init; }
    public required string[] CandidateKeys { get; init; }
    public required string[] GroupDigests { get; init; }
}

public sealed record ModelFrozen : EventPayload
{
    public required string ModelKey { get; init; }
    public required string DerivationId { get; init; }
    public required string Label { get; init; }
    public required string FrozenBy { get; init; }
}

public sealed record TrialBranchCreated : EventPayload
{
    public required string BranchId { get; init; }
    public required long BaseSeq { get; init; }
    public required Anchor TrialAnchor { get; init; }
    public required string Reason { get; init; }
}

/// <summary>Fork a new branch from the main branch at baseSeq without adding material.</summary>
public sealed record BranchForked : EventPayload
{
    public required string BranchId { get; init; }
    public required long BaseSeq { get; init; }
    public required string Reason { get; init; }
}

public sealed record ModelAccepted : EventPayload
{
    public required string ModelKey { get; init; }
    public required string DerivationId { get; init; }
    public required string DecidedBy { get; init; }
    public required string Rationale { get; init; }
}

public sealed record ModelRejected : EventPayload
{
    public required string ModelKey { get; init; }
    public required string DerivationId { get; init; }
    public required string DecidedBy { get; init; }
    public required string Rationale { get; init; }
}

/// <summary>
/// A human decision conflict (e.g. two reviewers accepting different candidates).
/// Both sides are preserved; resolution is explicit and machine-readable.
/// </summary>
public sealed record ConflictRecorded : EventPayload
{
    public required string ConflictId { get; init; }
    public required string Subject { get; init; }
    public required string[] Sides { get; init; }
    public required string Resolution { get; init; }
    public required string DecidedBy { get; init; }
}

/// <summary>
/// Machine-readable statement of a domain rule judgement, e.g. "monotonicity holds only
/// within continuous segments". Emitted so the counter-example archive keeps the rule
/// basis next to the evidence.
/// </summary>
public sealed record PolicyAsserted : EventPayload
{
    public required string PolicyId { get; init; }
    public required string Statement { get; init; }
    public required string[] EvidenceEventIds { get; init; }
}

/// <summary>Envelope around a payload with identity, branch and provenance metadata.</summary>
public sealed record EventEnvelope
{
    public required string EventId { get; init; }
    public required string BranchId { get; init; }
    public required string Actor { get; init; }
    public required string Timestamp { get; init; }
    public required EventPayload Payload { get; init; }
    public string? CorrelationId { get; init; }
    public string? CauseEventId { get; init; }

    public string PayloadKind => Payload.GetType().Name;

    public static EventEnvelope Of(EventPayload payload, string branchId, string actor, string timestamp,
        string? correlationId = null, string? causeEventId = null)
    {
        var content = new
        {
            branch = branchId,
            actor,
            ts = timestamp,
            payload = Canon.Serialize(payload)
        };
        return new EventEnvelope
        {
            EventId = Canon.StableId("evt", content),
            BranchId = branchId,
            Actor = actor,
            Timestamp = timestamp,
            Payload = payload,
            CorrelationId = correlationId,
            CauseEventId = causeEventId
        };
    }
}
