using System.Text.Json.Serialization;

namespace AgeModel.Core;

public sealed record EventEnvelope(
    string EventId,
    string ProjectId,
    long Sequence,
    string Timestamp,
    string EventType,
    string Branch,
    IReadOnlyList<string> ParentEventIds,
    string RuleVersion,
    string InputHash,
    string PayloadJson,
    string? BatchId = null);

public sealed record Diagnostic(
    string Code,
    string Severity,
    string Path,
    string? SubjectId,
    string Message,
    string? RawValue = null)
{
    public bool IsFailure => Severity == "error";
}

public sealed record DisplayValue(
    string Id,
    string Label,
    double? NumericValue,
    string Text,
    string Unit,
    string? Datum,
    string? EffectiveRange,
    string? FailureReason,
    string SourceEventId);

public sealed record AnchorView(
    AgeAnchor Anchor,
    string SegmentId,
    double DepthM,
    double MedianAge,
    double Q01,
    double Q05,
    double Q95,
    double Q99,
    bool Used,
    string? ExclusionReason);

public sealed record RateIntervalView(
    string Id,
    string CandidateId,
    string SegmentId,
    string FromAnchorId,
    string ToAnchorId,
    double TopDepthM,
    double BaseDepthM,
    double MedianRateCmPerKyr,
    double LowRateCmPerKyr,
    double HighRateCmPerKyr,
    double LongTailLowRateCmPerKyr,
    double LongTailHighRateCmPerKyr,
    string EffectiveRange,
    IReadOnlyList<string> SourceAnchorIds);

public sealed record BandPoint(
    string Id,
    string CandidateId,
    string AnchorId,
    double DepthM,
    double Q01,
    double Q05,
    double Median,
    double Q95,
    double Q99);

public sealed record CandidateModel(
    string Id,
    int Rank,
    bool TieWithNext,
    bool Adopted,
    bool Frozen,
    double Score,
    string MaterialHash,
    string RuleVersion,
    IReadOnlyList<string> IncludedAnchorIds,
    IReadOnlyList<string> ProposedExclusionIds,
    IReadOnlyList<string> SegmentIds,
    IReadOnlyList<RateIntervalView> Rates,
    IReadOnlyList<BandPoint> Band,
    IReadOnlyList<Diagnostic> Diagnostics);

public sealed record RejectedModel(
    string Id,
    string IncludedAnchorIdsHash,
    IReadOnlyList<string> IncludedAnchorIds,
    IReadOnlyList<string> Reasons,
    string RejectionCode,
    string ProbeId);

public sealed record ProbeArchiveEntry(
    string Id,
    string Kind,
    string Label,
    string BaselineEventId,
    string BaselineHash,
    string ChangedDirectSourceId,
    bool Adopted,
    string JudgmentCode,
    string JudgmentReason,
    IReadOnlyList<string> ExpectedAffectedIds,
    IReadOnlyList<string> ActualAffectedIds,
    bool ImpactContained,
    string InputFragment,
    IReadOnlyList<string> CandidateIds,
    IReadOnlyList<string> RejectedModelIds,
    IReadOnlyList<Diagnostic> Diagnostics,
    string RuleVersion,
    string ParentEventId);

public sealed record EngineResult(
    ProjectStatus Status,
    string MaterialHash,
    RuleParameters Parameters,
    IReadOnlyList<Diagnostic> Diagnostics,
    IReadOnlyList<AnchorView> Anchors,
    IReadOnlyList<CandidateModel> Candidates,
    IReadOnlyList<RejectedModel> RejectedModels,
    IReadOnlyList<RateIntervalView> AffectedRates)
{
    public bool Succeeded => Status == ProjectStatus.Ok;
}

public sealed record ImportResult(
    ProjectStatus Status,
    CasePackage? Package,
    IReadOnlyList<Diagnostic> Diagnostics)
{
    public bool Succeeded => Status == ProjectStatus.Ok && Package is not null;
}
