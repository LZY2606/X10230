namespace DepthAge.Core.Cases;

/// <summary>
/// 离线领域案例包：岩芯年代尺（深度锚点、沉积速率与多候选模型谱系）。
/// 不是通用表格：字段全部围绕深度基准/分段缺口/放射性年龄分布/储层偏移/事件层相关。
/// </summary>
public sealed record CasePackage
{
    public required string PackageFormat { get; init; }      // depth-age-case/1
    public required string CaseId { get; init; }
    public required string Title { get; init; }
    public required string Narrative { get; init; }
    public required string ProjectName { get; init; }
    public List<CaseDatum> Datums { get; init; } = new();
    public List<CaseSection> Sections { get; init; } = new();
    public List<CaseCalibrationCurve> CalibrationCurves { get; init; } = new();
    public List<CaseReservoir> ReservoirOffsets { get; init; } = new();
    public List<CaseEventBed> EventBeds { get; init; } = new();
    public List<CaseEventLink> EventLinks { get; init; } = new();
    public List<CaseAnchor> Anchors { get; init; } = new();
    public List<CaseExclusion> BaselineExclusions { get; init; } = new();
    /// <summary>叙事化重放：一次参数修订、一次局部源数据改动、一次互相冲突的人工决定。</summary>
    public List<ReplayStep> Replay { get; init; } = new();
    public List<CaseFixture> Fixtures { get; init; } = new();
}

public sealed record CaseDatum(string Id, string Name, string CoordinateSystem, string CanonicalUnit, double ZeroOffsetM, string? SourceRef);

public sealed record CaseSection(string Id, string CoreId, double TopM, double BottomM, string BottomBoundary, string? BoundaryNote, string? SourceRef);

public sealed record CaseCalibrationCurve(string Id, string Name, List<CaseCalPoint> Points, string? SourceRef);
public sealed record CaseCalPoint(double RadiocarbonYrBP, double CalYrBP, double SigmaCalYr = 80);

public sealed record CaseReservoir(string Id, string Name, double DeltaRYr, double SigmaYr, double TopM, double BottomM, string? SourceRef);

public sealed record CaseEventBed(string Id, string Name, double MeanCalYrBP, double SigmaYr, string? SourceRef);

public sealed record CaseEventLink(string Id, string EventBedId, string CoreId, double DepthM, string DatumId, double? ToleranceM, string? SourceRef);

public sealed record CaseAnchor(
    string Id, string Label, string CoreId, double Depth, string DepthUnit, string DatumId,
    string AgeKind, double ReportedAge, string AgeUnit, double? Sigma,
    string? CalibrationCurveId, string? EventBedId, string? Method, string? SourceRef,
    bool LongTail = false, List<double>? PresetSamples = null, string? DepthRawOverride = null);

public sealed record CaseExclusion(string Id, string TargetAnchorId, string Reason, string Basis, string DecidedBy);

public sealed record ReplayStep(
    string Id, string Kind, string Description,
    string? AnchorId = null, string? Field = null, object? Value = null,
    int? CandidateBudget = null, double? TieTolerance = null, string? Note = null,
    string? AcceptCandidateMethod = null, string? RejectCandidateMethod = null,
    string? BaselineRef = null, string? SourceId = null, string? ExpectScope = null,
    bool? FreezeFirst = null);

public sealed record CaseFixture(string Id, string Kind, string Description, string Expectation);
