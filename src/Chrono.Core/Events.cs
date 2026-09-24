using System.Text.Json.Serialization;

namespace Chrono.Core;

public enum EventType
{
    ProjectCreated, MaterialsImported, SourceReplaced, ParameterRevised,
    Derived, DecisionRecorded, ModelFrozen, BranchCreated,
    FindingRecorded, CommandRejected
}

[JsonPolymorphic(TypeDiscriminatorPropertyName = "payloadType")]
[JsonDerivedType(typeof(ProjectCreatedPayload), "project-created")]
[JsonDerivedType(typeof(MaterialsPayload), "materials")]
[JsonDerivedType(typeof(SourceReplacedPayload), "source-replaced")]
[JsonDerivedType(typeof(ParameterPayload), "parameters")]
[JsonDerivedType(typeof(DerivedPayload), "derived")]
[JsonDerivedType(typeof(DecisionPayload), "decision")]
[JsonDerivedType(typeof(FrozenPayload), "frozen")]
[JsonDerivedType(typeof(BranchPayload), "branch")]
[JsonDerivedType(typeof(FindingPayload), "finding")]
[JsonDerivedType(typeof(RejectedPayload), "rejected")]
public abstract record EventPayload;

public record ProjectCreatedPayload(string Name, string CaseId) : EventPayload;
public record MaterialsPayload(MaterialSet Materials) : EventPayload;
public record SourceReplacedPayload(string DocumentName, string PriorRevisionId, MaterialSet Materials) : EventPayload;
public record ParameterPayload(ParameterSet Parameters) : EventPayload;
public record DerivedPayload(DerivationResult Result) : EventPayload;
public record DecisionPayload(string TargetCandidateId, string Action, string Note,
    string? SupersedesEventId, bool Conflicts) : EventPayload;
public record FrozenPayload(string CandidateId, string Note) : EventPayload;
public record BranchPayload(string NewBranchId, string FromBranchId, int FromEventSeq, string Reason) : EventPayload;
public record FindingPayload(string FindingKind, IReadOnlyList<string> AffectedIds) : EventPayload;
public record RejectedPayload(string Command, string Reason, string? ExpectedVersion) : EventPayload;

/// <summary>Append-only event. Identity is stable across export/import; content is hash-addressed.</summary>
public sealed record ChronoEvent
{
    public string EventId { get; init; } = "";
    public string ProjectId { get; init; } = "";
    public string BranchId { get; init; } = "main";
    public int Seq { get; init; }
    public string EventType { get; init; } = "";
    public string Author { get; init; } = "reviewer";
    public string CreatedAtUtc { get; init; } = "";
    public string InputDigest { get; init; } = "";
    public string ParentEventId { get; init; } = "";
    public EventPayload Payload { get; init; } = null!;
}
