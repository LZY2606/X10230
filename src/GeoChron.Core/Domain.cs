using System.Text.Json;
using System.Text.Json.Serialization;

namespace GeoChron.Core;

/// <summary>深度基准（不可变材料）。所有深度必须声明坐标基准。</summary>
public sealed record DepthDatum(
    [property: JsonPropertyName("id")] string Id,
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("verticalDatum")] string VerticalDatum,
    [property: JsonPropertyName("zeroDepth")] string ZeroDepth,
    [property: JsonPropertyName("depthDirection")] string DepthDirection,
    [property: JsonPropertyName("depthUnit")] string DepthUnit,
    [property: JsonPropertyName("corrections")] string Corrections,
    [property: JsonPropertyName("sourceRef")] string SourceRef);

/// <summary>岩芯分段（不可变材料）。</summary>
public sealed record Segment(
    [property: JsonPropertyName("id")] string Id,
    [property: JsonPropertyName("label")] string Label,
    [property: JsonPropertyName("topDepth")] Quantity TopDepth,
    [property: JsonPropertyName("bottomDepth")] Quantity BottomDepth,
    [property: JsonPropertyName("lithology")] string Lithology,
    [property: JsonPropertyName("sourceRef")] string SourceRef);

/// <summary>分段缺口/不整合：显式边界。年龄单调性不得跨越此类边界。</summary>
public sealed record Gap(
    [property: JsonPropertyName("id")] string Id,
    [property: JsonPropertyName("label")] string Label,
    [property: JsonPropertyName("atOrBelowDepth")] Quantity AtOrBelowDepth,
    [property: JsonPropertyName("kind")] string Kind,
    [property: JsonPropertyName("justification")] string Justification,
    [property: JsonPropertyName("sourceRef")] string SourceRef);

/// <summary>放射性年龄分布。Raw 保留导入时的原始 JSON 片段，派生层才规范化。</summary>
public sealed record AgeDistributionInfo(
    [property: JsonPropertyName("kind")] string Kind,
    [property: JsonPropertyName("ageUnit")] string? AgeUnit,
    [property: JsonPropertyName("mean")] Quantity? Mean,
    [property: JsonPropertyName("sd")] Quantity? Sd,
    [property: JsonPropertyName("min")] Quantity? Min,
    [property: JsonPropertyName("max")] Quantity? Max,
    [property: JsonPropertyName("median")] Quantity? Median,
    [property: JsonPropertyName("sigmaLog")] double? SigmaLog,
    [property: JsonPropertyName("members")] IReadOnlyList<AgeDistributionInfo>? Members,
    [property: JsonPropertyName("weights")] IReadOnlyList<double>? Weights,
    [property: JsonPropertyName("samples")] IReadOnlyList<Quantity>? Samples,
    [property: JsonPropertyName("raw")] JsonElement Raw);

/// <summary>年龄锚点：一个锚点携带完整分布，禁止以均值替代。</summary>
public sealed record Anchor(
    [property: JsonPropertyName("id")] string Id,
    [property: JsonPropertyName("label")] string Label,
    [property: JsonPropertyName("depth")] Quantity Depth,
    [property: JsonPropertyName("ageDistribution")] AgeDistributionInfo Distribution,
    [property: JsonPropertyName("method")] string Method,
    [property: JsonPropertyName("material")] string Material,
    [property: JsonPropertyName("sampleRef")] string SampleRef,
    [property: JsonPropertyName("correlatedEventId")] string? CorrelatedEventId,
    [property: JsonPropertyName("reservoirOffsetId")] string? ReservoirOffsetId,
    [property: JsonPropertyName("status")] string Status = "active",
    [property: JsonPropertyName("exclusionReason")] string? ExclusionReason = null);

/// <summary>储层偏移（如海洋碳库 R）。</summary>
public sealed record ReservoirOffset(
    [property: JsonPropertyName("id")] string Id,
    [property: JsonPropertyName("label")] string Label,
    [property: JsonPropertyName("value")] Quantity Value,
    [property: JsonPropertyName("ageUnit")] string? AgeUnit,
    [property: JsonPropertyName("rationale")] string Rationale,
    [property: JsonPropertyName("sourceRef")] string SourceRef);

/// <summary>事件层及其对比依据。</summary>
public sealed record EventLayer(
    [property: JsonPropertyName("id")] string Id,
    [property: JsonPropertyName("label")] string Label,
    [property: JsonPropertyName("depth")] Quantity Depth,
    [property: JsonPropertyName("eventCode")] string EventCode,
    [property: JsonPropertyName("correlationBasis")] string CorrelationBasis,
    [property: JsonPropertyName("olderThanEventId")] string? OlderThanEventId,
    [property: JsonPropertyName("youngerThanEventId")] string? YoungerThanEventId,
    [property: JsonPropertyName("ageDistribution")] AgeDistributionInfo? AgeDistribution,
    [property: JsonPropertyName("sourceRef")] string SourceRef);

/// <summary>排除决定本身也是受保留材料。</summary>
public sealed record ExclusionDecision(
    [property: JsonPropertyName("id")] string Id,
    [property: JsonPropertyName("subjectId")] string SubjectId,
    [property: JsonPropertyName("subjectKind")] string SubjectKind,
    [property: JsonPropertyName("reason")] string Reason,
    [property: JsonPropertyName("decidedBy")] string DecidedBy,
    [property: JsonPropertyName("basis")] string Basis);

/// <summary>建模参数（可修订；每次修订产生新版本与事件）。</summary>
public sealed record ModelParams(
    [property: JsonPropertyName("version")] int Version,
    [property: JsonPropertyName("interpolation")] string Interpolation,
    [property: JsonPropertyName("monotonic")] string Monotonic,
    [property: JsonPropertyName("minRateCmPerKa")] double MinRateCmPerKa,
    [property: JsonPropertyName("mcBudget")] int McBudget,
    [property: JsonPropertyName("gridStepCm")] double GridStepCm,
    [property: JsonPropertyName("tieToleranceKa")] double TieToleranceKa,
    [property: JsonPropertyName("tailQuantiles")] IReadOnlyList<double> TailQuantiles);

/// <summary>离线案例包 v1：领域材料而非通用表格。</summary>
public sealed record CasePack(
    [property: JsonPropertyName("schema")] string Schema,
    [property: JsonPropertyName("caseId")] string CaseId,
    [property: JsonPropertyName("title")] string Title,
    [property: JsonPropertyName("narrative")] string Narrative,
    [property: JsonPropertyName("depthDatum")] DepthDatum DepthDatum,
    [property: JsonPropertyName("segments")] IReadOnlyList<Segment> Segments,
    [property: JsonPropertyName("gaps")] IReadOnlyList<Gap> Gaps,
    [property: JsonPropertyName("anchors")] IReadOnlyList<Anchor> Anchors,
    [property: JsonPropertyName("reservoirOffsets")] IReadOnlyList<ReservoirOffset> ReservoirOffsets,
    [property: JsonPropertyName("eventLayers")] IReadOnlyList<EventLayer> EventLayers,
    [property: JsonPropertyName("exclusions")] IReadOnlyList<ExclusionDecision> Exclusions,
    [property: JsonPropertyName("params")] ModelParams Params);

public static class CaseJson
{
    public static readonly JsonSerializerOptions Options = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };
}
