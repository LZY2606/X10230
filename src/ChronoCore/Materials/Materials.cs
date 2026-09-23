using ChronoCore.Quantities;

namespace ChronoCore.Materials;

public enum BoundaryKind { Gap = 0, Unconformity = 1 }

public enum AnchorKind
{
    Radiocarbon = 0,
    Tephra = 1,
    Biostrat = 2,
    Radiometric = 3,
    Historical = 4,
}

public enum ExclusionReason
{
    Other = 0,
    DepthNotConvertible = 1,
    AgeBasisIncompatible = 2,
    ReworkedMaterial = 3,
    DuplicateSample = 4,
    OutsideProjectScope = 5,
    InvertedRelativeAge = 6,
}

/// <summary>一段连续岩芯（不断层段）。年龄单调性只在段内成立。</summary>
public sealed record CoreSection(
    string Id,
    string Code,
    double TopM,
    double BottomM,
    string DatumId,
    string Lithology,
    string SourcePath,
    string? Note);

/// <summary>显式边界：深度缺口或不整合。模型不得跨边界插值或判单调。</summary>
public sealed record SegmentBoundary(
    string Id,
    double DepthM,
    string DatumId,
    BoundaryKind Kind,
    double? MissingSectionM,
    string SourcePath,
    string? Note);

/// <summary>
/// 年龄锚点（不可变材料）。RawDepth/RawAgeText 保留原始表示；RadiocarbonAgeBp 为 14C 年龄原文，
/// CalibratedAge 是经曲线+储层校正后的派生分布；两者都保留。
/// </summary>
public sealed record AnchorMaterial
{
    public required string Id { get; init; }
    public required string Label { get; init; }
    public required AnchorKind Kind { get; init; }
    public required RawDepth RawDepth { get; init; }
    public required string DatumId { get; init; }
    public double? DepthM { get; init; }
    public string? RawAgeText { get; init; }
    public string? ReservoirOffsetId { get; init; }
    public double? RadiocarbonAgeBp { get; init; }
    public double? RadiocarbonSigma { get; init; }
    public string? CalibrationCurveId { get; init; }
    public AgeDistribution? RawMeasuredAge { get; init; }
    public AgeDistribution? CalibratedAge { get; init; }
    public string? AgeDiagnostic { get; init; }
    public string? EventLayerId { get; init; }
    public required string SourcePath { get; init; }
    public string? Note { get; init; }

    public bool Usable => DepthM is not null && CalibratedAge is not null && AgeDiagnostic is null;
}

/// <summary>事件层（火山灰、生物事件等），可与多个锚点相关；相关本身是不可变材料。</summary>
public sealed record EventLayer
{
    public required string Id { get; init; }
    public required string Name { get; init; }
    public required string EventType { get; init; }
    public required RawDepth RawDepth { get; init; }
    public double? DepthM { get; init; }
    public required string DatumId { get; init; }
    public AgeDistribution? EventAge { get; init; }
    public string? CorrelationTo { get; init; }
    public required string SourcePath { get; init; }
    public string? Note { get; init; }
}

/// <summary>排除决定：被排除材料与理由都保留，可追溯、可翻案。</summary>
public sealed record ExclusionRecord
{
    public required string Id { get; init; }
    public required string SubjectMaterialId { get; init; }
    public required ExclusionReason Reason { get; init; }
    public required string DecidedBy { get; init; }
    public required string DecisionEventId { get; init; }
    public string? Detail { get; init; }
}

/// <summary>一次导入的完整材料包（不可变材料层）。</summary>
public sealed record MaterialBundle(
    string BundleId,
    DepthDatum Datum,
    IReadOnlyList<BenchmarkTie> Ties,
    IReadOnlyList<CoreSection> Sections,
    IReadOnlyList<SegmentBoundary> Boundaries,
    IReadOnlyList<EventLayer> EventLayers,
    IReadOnlyList<AnchorMaterial> Anchors,
    IReadOnlyList<ExclusionRecord> Exclusions,
    IReadOnlyList<ReservoirOffset> ReservoirOffsets);
