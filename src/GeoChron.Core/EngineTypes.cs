using System.Text.Json.Serialization;

namespace GeoChron.Core;

public enum RunOutcome
{
    /// <summary>成功：得到一个或多个合法候选。</summary>
    Ok = 0,
    /// <summary>格式损坏：无法解析的输入。</summary>
    FormatCorrupt = 1,
    /// <summary>语义无解：材料无法支撑任何合法深度-年龄模型。</summary>
    SemanticUnresolved = 2,
    /// <summary>资源预算耗尽：候选爆炸或蒙特卡洛预算不足。</summary>
    BudgetExhausted = 3
}

public enum FindingKind
{
    DepthDuplicate,
    TieGroup,
    InvertedInterval,
    SegmentInverted,
    GapBoundary,
    LongTail,
    ReservoirApplied,
    ExcludedAnchor,
    NonConvertibleValue,
    LocalRecompute,
    FreezeConflict
}

public sealed record Finding(
    [property: JsonPropertyName("kind")] FindingKind Kind,
    [property: JsonPropertyName("subjectIds")] IReadOnlyList<string> SubjectIds,
    [property: JsonPropertyName("sectionId")] string? SectionId,
    [property: JsonPropertyName("depthCm")] double? DepthCm,
    [property: JsonPropertyName("message")] string Message,
    [property: JsonPropertyName("detail")] IReadOnlyDictionary<string, string>? Detail = null);

public sealed record Section(
    [property: JsonPropertyName("id")] string Id,
    [property: JsonPropertyName("index")] int Index,
    [property: JsonPropertyName("topCm")] double TopCm,
    [property: JsonPropertyName("bottomCm")] double BottomCm,
    [property: JsonPropertyName("segmentIds")] IReadOnlyList<string> SegmentIds,
    [property: JsonPropertyName("lowerBoundaryGapId")] string? LowerBoundaryGapId);

public sealed record RateInterval(
    [property: JsonPropertyName("sectionId")] string SectionId,
    [property: JsonPropertyName("fromDepthCm")] double FromDepthCm,
    [property: JsonPropertyName("toDepthCm")] double ToDepthCm,
    [property: JsonPropertyName("q025")] double? Q025,
    [property: JsonPropertyName("q500")] double? Q500,
    [property: JsonPropertyName("q975")] double? Q975,
    [property: JsonPropertyName("unit")] string Unit,
    [property: JsonPropertyName("notEstimableReason")] string? NotEstimableReason = null);

public sealed record BandPoint(
    [property: JsonPropertyName("sectionId")] string SectionId,
    [property: JsonPropertyName("depthCm")] double DepthCm,
    [property: JsonPropertyName("quantiles")] IReadOnlyDictionary<double, double> Quantiles);

public sealed record CandidateModel
{
    [JsonPropertyName("id")] public required string Id { get; init; }
    [JsonPropertyName("rank")] public required int Rank { get; init; }
    [JsonPropertyName("status")] public required string Status { get; init; } // legal | rejected | frozen
    [JsonPropertyName("strategy")] public required string Strategy { get; init; }
    [JsonPropertyName("strategyDetail")] public required IReadOnlyDictionary<string, string> StrategyDetail { get; init; }
    [JsonPropertyName("score")] public required double Score { get; init; }
    [JsonPropertyName("equalOptimal")] public required bool EqualOptimal { get; init; }
    [JsonPropertyName("rejectionReason")] public string? RejectionReason { get; init; }
    [JsonPropertyName("parentDependencies")] public required IReadOnlyList<string> ParentDependencies { get; init; }
    [JsonPropertyName("paramsVersion")] public required int ParamsVersion { get; init; }
    [JsonPropertyName("inputSummary")] public required string InputSummary { get; init; }
    [JsonPropertyName("usedAnchorIds")] public required IReadOnlyList<string> UsedAnchorIds { get; init; }
    [JsonPropertyName("excludedAnchorIds")] public required IReadOnlyList<string> ExcludedAnchorIds { get; init; }
    [JsonPropertyName("insertedGapIds")] public required IReadOnlyList<string> InsertedGapIds { get; init; }
    [JsonPropertyName("rateIntervals")] public required IReadOnlyList<RateInterval> RateIntervals { get; init; }
    [JsonPropertyName("band")] public required IReadOnlyList<BandPoint> Band { get; init; }
    [JsonPropertyName("monotoneViolationRate")] public required double MonotoneViolationRate { get; init; }
}

public sealed record EngineOutput(
    [property: JsonPropertyName("outcome")] RunOutcome Outcome,
    [property: JsonPropertyName("inputSummary")] string InputSummary,
    [property: JsonPropertyName("paramsVersion")] int ParamsVersion,
    [property: JsonPropertyName("diagnostics")] IReadOnlyList<Diagnostic> Diagnostics,
    [property: JsonPropertyName("findings")] IReadOnlyList<Finding> Findings,
    [property: JsonPropertyName("sections")] IReadOnlyList<Section> Sections,
    [property: JsonPropertyName("candidates")] IReadOnlyList<CandidateModel> Candidates,
    [property: JsonPropertyName("mcDraws")] int McDraws,
    [property: JsonPropertyName("candidateCount")] int CandidateCount,
    [property: JsonPropertyName("budget")] ComputeBudget Budget);

public sealed record ComputeBudget(
    [property: JsonPropertyName("mcBudget")] int McBudget,
    [property: JsonPropertyName("mcUsed")] int McUsed,
    [property: JsonPropertyName("candidateBudget")] int CandidateBudget,
    [property: JsonPropertyName("candidatesEnumerated")] int CandidatesEnumerated,
    [property: JsonPropertyName("exhausted")] bool Exhausted,
    [property: JsonPropertyName("reason")] string? Reason = null);
