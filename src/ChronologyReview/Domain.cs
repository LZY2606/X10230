using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace ChronologyReview;

public static class Json
{
    public static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() }
    };

    public static string StableHash<T>(T value)
    {
        var json = JsonSerializer.Serialize(value, Options);
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(json))).ToLowerInvariant();
    }
}

public sealed record DepthDatum(
    string Id,
    string Name,
    string Unit,
    string PositiveDirection,
    string Reference);

public sealed record Project(
    string ProjectId,
    string Name,
    DepthDatum Datum,
    string RuleVersion,
    string ParameterVersion);

public sealed record CoreSegment(
    string SegmentId,
    string Label,
    double TopDepth,
    double BottomDepth,
    string Unit,
    string Lithology,
    bool ExplicitBoundaryAbove,
    bool ExplicitBoundaryBelow);

public sealed record Boundary(
    string BoundaryId,
    string Kind,
    double FromDepth,
    double ToDepth,
    string Unit,
    string Reason);

public sealed record AgeAnchor(
    string AnchorId,
    string SegmentId,
    double Depth,
    string RawDepth,
    string DepthUnit,
    string DatingMethod,
    IReadOnlyList<double> AgeDistributionKaBp,
    string RawAgeRepresentation,
    double ReservoirOffsetKa,
    string? EventLayerId,
    string Source);

public sealed record EventLayer(
    string EventLayerId,
    string SegmentId,
    double Depth,
    string RawDepth,
    string Unit,
    string Name,
    string Correlation);

public sealed record ExclusionDecision(
    string DecisionId,
    string TargetId,
    string TargetKind,
    string Reason,
    string Actor,
    string RuleVersion,
    bool Active);

public sealed record HumanDecision(
    string DecisionId,
    string BranchId,
    int Sequence,
    string? ParentDecisionId,
    string Kind,
    string TargetId,
    string Status,
    string Reason,
    string Actor,
    string ParameterVersion,
    DateTimeOffset CreatedAt);

public sealed record Diagnostic(
    string Code,
    string Severity,
    string Message,
    string EntityId,
    double? Depth,
    string Unit,
    IReadOnlyList<string> AffectedScopes);

public sealed record RateInterval(
    string IntervalId,
    string SegmentId,
    double FromDepth,
    double ToDepth,
    double RateKaPerM,
    string Unit,
    string FromAnchorId,
    string ToAnchorId);

public sealed record UncertaintyPoint(
    string PointId,
    string AnchorId,
    double Depth,
    double P05,
    double Median,
    double P95,
    string AgeUnit,
    string DepthUnit);

public sealed record ModelCandidate(
    string CandidateId,
    string BlockId,
    IReadOnlyList<string> AnchorIds,
    IReadOnlyList<RateInterval> Rates,
    IReadOnlyList<UncertaintyPoint> Uncertainty,
    double FitScore,
    string Rationale);

public enum RunStatus
{
    Success,
    BudgetExhausted,
    FormatCorrupt,
    SemanticUnsolved
}

public sealed record ModelResult(
    RunStatus Status,
    IReadOnlyList<ModelCandidate> Candidates,
    IReadOnlyList<Diagnostic> Diagnostics,
    IReadOnlyList<string> AffectedScopes,
    string FailureReason);

public sealed record ModelRun(
    string RunId,
    string ProjectId,
    string BranchId,
    string? ParentRunId,
    string ParameterVersion,
    string RuleVersion,
    string InputHash,
    ModelResult Result,
    string CreatedEventId,
    DateTimeOffset CreatedAt,
    bool Frozen);

public sealed record ChronologyEvent(
    string EventId,
    string ProjectId,
    string BranchId,
    int Sequence,
    string Kind,
    string EntityId,
    string PayloadJson,
    string RuleVersion,
    string InputHash,
    string Status,
    DateTimeOffset CreatedAt);

public sealed record MaterialRecord(
    string MaterialId,
    string Kind,
    int Ordinal,
    string JsonPayload,
    string SourceHash);

public sealed record ProjectSnapshot(
    Project Project,
    IReadOnlyList<CoreSegment> Segments,
    IReadOnlyList<Boundary> Boundaries,
    IReadOnlyList<AgeAnchor> Anchors,
    IReadOnlyList<EventLayer> EventLayers,
    IReadOnlyList<ExclusionDecision> Exclusions,
    IReadOnlyList<HumanDecision> Decisions,
    IReadOnlyList<ModelRun> Runs,
    IReadOnlyList<ChronologyEvent> Events);

public sealed record ExportBundle(
    int SchemaVersion,
    string ExportedAt,
    Project Project,
    IReadOnlyList<MaterialRecord> Materials,
    IReadOnlyList<HumanDecision> Decisions,
    IReadOnlyList<ModelRun> Runs,
    IReadOnlyList<ChronologyEvent> Events);

public sealed record AnchorRequest(
    string AnchorId,
    string SegmentId,
    double Depth,
    string RawDepth,
    string DepthUnit,
    string DatingMethod,
    IReadOnlyList<double> AgeDistributionKaBp,
    string RawAgeRepresentation,
    double ReservoirOffsetKa,
    string? EventLayerId,
    string Source);

public sealed record DecisionRequest(
    string DecisionId,
    string BranchId,
    string Kind,
    string TargetId,
    string Status,
    string Reason,
    string Actor);

public sealed record ImportResult(
    bool Success,
    RunStatus FailureKind,
    string Message,
    ProjectSnapshot? Snapshot);

public static class Ids
{
    public static string New(string prefix) => $"{prefix}-{Guid.NewGuid():N}";
}
