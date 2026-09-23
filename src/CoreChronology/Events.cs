namespace CoreChronology;

public enum EventKind
{
    ProjectCreated,
    MaterialsCommitted,
    ParameterRevision,
    ExclusionDecided,
    ExclusionRevoked,
    FreezeRequested,
    ManualDecision,
}

public enum DecisionVerdict { Accept, Reject, ParameterAdjustment }

/// <summary>Append-only event. Events are never mutated; corrections are new versions.</summary>
public sealed record ProjectEvent
{
    public required string Id { get; init; }
    public required string ProjectId { get; init; }
    public required string BranchId { get; init; }
    public required long Seq { get; init; }
    public required EventKind Kind { get; init; }
    /// <summary>DAG parent events (usually one; merges/forks may reference two).</summary>
    public required IReadOnlyList<string> ParentEventIds { get; init; }
    public required DateTimeOffset RecordedAt { get; init; }
    public required string Actor { get; init; }
    public required string PayloadJson { get; init; }
    public string? Rationale { get; init; }
}

public sealed record ProjectCreatedPayload(
    string ProjectId,
    string CoreLabel,
    string DepthDatumId,
    string CoordinateBasis);

public sealed record MaterialsCommittedPayload(
    string BatchId,
    IReadOnlyList<MaterialVersion> Versions,
    IReadOnlyList<ImportDiagnostic> Diagnostics);

public sealed record MaterialVersion(string LogicalId, int Version, string Kind, string MaterialJson);

public sealed record ImportDiagnostic(
    string Code, string Message, string? SourceRef, int? SourceLine, string? MaterialId);

public sealed record ParameterRevisionPayload(ModelParameters Parameters);

public sealed record ExclusionPayload(string MaterialId, string ReasonCode, string Detail);

public sealed record FreezePayload(string Scope, string? ModelHash, string Reason);

public sealed record ManualDecisionPayload(
    DecisionVerdict Verdict,
    string TargetType,
    string TargetId,
    string Detail,
    string? ConflictsWith);
