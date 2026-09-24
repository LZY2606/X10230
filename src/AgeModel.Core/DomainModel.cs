using System.Text.Json.Serialization;

namespace AgeModel.Core;

public enum SegmentBoundaryType { Continuous, Hiatus, Gap, Fault, CorrelationBreak }

/// <summary>岩芯分段。缺口/不整合是显式边界（R2），不是连续插值的一部分。</summary>
public sealed record Segment(
    [property: JsonPropertyName("id")] string Id,
    [property: JsonPropertyName("topM")] double TopM,
    [property: JsonPropertyName("bottomM")] double BottomM,
    [property: JsonPropertyName("lowerBoundary")] SegmentBoundaryType LowerBoundary,
    [property: JsonPropertyName("overturned")] bool Overturned = false,
    [property: JsonPropertyName("label")] string Label = "");

/// <summary>储层年代偏移（reservoir age offset），自身带分布与参考材料。</summary>
public sealed record ReservoirCorrection(
    [property: JsonPropertyName("id")] string Id,
    [property: JsonPropertyName("meanKa")] double MeanKa,
    [property: JsonPropertyName("sigmaKa")] double SigmaKa,
    [property: JsonPropertyName("seed")] int Seed,
    [property: JsonPropertyName("basis")] string Basis);

/// <summary>放射性/事件年龄锚点。深度与年龄都带原始表示；年龄以分布存在（R3）。</summary>
public sealed class AgeAnchor
{
    [JsonPropertyName("id")] public string Id { init; get; } = "";
    [JsonPropertyName("segmentId")] public string SegmentId { init; get; } = "";
    [JsonPropertyName("depthRaw")] public string DepthRaw { init; get; } = "";
    [JsonPropertyName("depthM")] public double? DepthM { init; get; }
    [JsonPropertyName("method")] public string Method { init; get; } = "";
    [JsonPropertyName("ageDistribution")] public AgeDistribution? AgeDistribution { init; get; }
    [JsonPropertyName("reservoirCorrectionId")] public string? ReservoirCorrectionId { init; get; }
    [JsonPropertyName("eventId")] public string? EventId { init; get; }
    [JsonPropertyName("sourceRef")] public string SourceRef { init; get; } = "";
    [JsonPropertyName("notes")] public string? Notes { init; get; }
    [JsonPropertyName("excluded")] public bool Excluded { init; get; }
    [JsonPropertyName("exclusionReason")] public string? ExclusionReason { init; get; }
}

/// <summary>事件层（火山灰/同位素阶段等），可与锚点相关。</summary>
public sealed record EventHorizon(
    [property: JsonPropertyName("id")] string Id,
    [property: JsonPropertyName("label")] string Label,
    [property: JsonPropertyName("kind")] string Kind,
    [property: JsonPropertyName("depthRaw")] string? DepthRaw,
    [property: JsonPropertyName("depthM")] double? DepthM,
    [property: JsonPropertyName("correlatedAnchorId")] string? CorrelatedAnchorId,
    [property: JsonPropertyName("basis")] string Basis);

/// <summary>排除决定本身也是不可变材料，必须可追溯。</summary>
public sealed record ExclusionDecision(
    [property: JsonPropertyName("id")] string Id,
    [property: JsonPropertyName("targetKind")] string TargetKind,
    [property: JsonPropertyName("targetId")] string TargetId,
    [property: JsonPropertyName("reason")] string Reason,
    [property: JsonPropertyName("sourceRef")] string SourceRef,
    [property: JsonPropertyName("active")] bool Active = true);

/// <summary>深度基准声明。</summary>
public sealed record DepthDatumDeclaration(
    [property: JsonPropertyName("id")] string Id,
    [property: JsonPropertyName("datum")] string Datum,
    [property: JsonPropertyName("raw")] string Raw,
    [property: JsonPropertyName("basis")] string Basis);

/// <summary>不可变材料包：深度基准、分段缺口、年龄分布、储层偏移、事件层、排除决定全部保留。</summary>
public sealed class CoreMaterial
{
    [JsonPropertyName("coreId")] public string CoreId { init; get; } = "";
    [JsonPropertyName("coreName")] public string CoreName { init; get; } = "";
    [JsonPropertyName("datums")] public List<DepthDatumDeclaration> Datums { init; get; } = new();
    [JsonPropertyName("segments")] public List<Segment> Segments { init; get; } = new();
    [JsonPropertyName("reservoirCorrections")] public List<ReservoirCorrection> ReservoirCorrections { init; get; } = new();
    [JsonPropertyName("anchors")] public List<AgeAnchor> Anchors { init; get; } = new();
    [JsonPropertyName("events")] public List<EventHorizon> Events { init; get; } = new();
    [JsonPropertyName("exclusions")] public List<ExclusionDecision> Exclusions { init; get; } = new();
}

public enum DiagnosticSeverity { Error, Warning, Info }

/// <summary>带位置的诊断。输入不完整时返回诊断，禁止用空值/默认值伪装成功。</summary>
public sealed record Diagnostic(
    [property: JsonPropertyName("severity")] DiagnosticSeverity Severity,
    [property: JsonPropertyName("code")] string Code,
    [property: JsonPropertyName("path")] string Path,
    [property: JsonPropertyName("message")] string Message,
    [property: JsonPropertyName("targetId")] string? TargetId = null);

public static class DiagnosticCodes
{
    public const string UnconvertibleDepth = "DEPTH_UNCONVERTIBLE";
    public const string UnconvertibleAge = "AGE_UNCONVERTIBLE";
    public const string DuplicateDepth = "DEPTH_DUPLICATE";
    public const string AnchorOutsideSegment = "ANCHOR_OUTSIDE_SEGMENT";
    public const string MonotonicityViolation = "AGE_MONOTONICITY_VIOLATION";
    public const string Inversion = "AGE_INVERSION";
    public const string TiedAnchors = "ANCHORS_TIED";
    public const string SegmentOverlap = "SEGMENT_OVERLAP";
    public const string SegmentGapMisordered = "SEGMENT_ORDER";
    public const string MissingReservoir = "MISSING_RESERVOIR_CORRECTION";
    public const string ExcludedAnchor = "ANCHOR_EXCLUDED";
    public const string IncompleteInput = "INCOMPLETE_INPUT";
    public const string LongTail = "DISTRIBUTION_LONG_TAIL";
    public const string Overturned = "SEGMENT_OVERTURNED";
    public const string SemanticUnresolvable = "SEMANTIC_UNRESOLVABLE";
    public const string BudgetExhausted = "BUDGET_EXHAUSTED";
    public const string FormatCorrupt = "FORMAT_CORRUPT";
}
