using System.Text.Json.Serialization;

namespace Chrono.CasePack;

/// <summary>
/// 离线领域案例包：岩芯年代尺（深度锚点、沉积速率与多候选模型谱系）。
/// 不是通用表格——每个字段都对应地层年代工作中的一类不可变材料。
/// </summary>
public sealed class CasePack
{
    public string Schema { get; set; } = "chrono-core-caseref/1";
    public string PackageId { get; set; } = "";
    public string Title { get; set; } = "";
    public string Narrative { get; set; } = "";
    public DepthDatumDto DepthDatum { get; set; } = new();
    public List<SectionDto> Sections { get; set; } = new();
    public List<BoundaryDto> Boundaries { get; set; } = new();
    public List<AnchorDto> Anchors { get; set; } = new();
    public List<ReservoirDto> Reservoirs { get; set; } = new();
    public List<CurveDto> Curves { get; set; } = new();
    public List<EventBedDto> EventBeds { get; set; } = new();
    public List<ExclusionDto> Exclusions { get; set; } = new();
    public ModelParamsDto Parameters { get; set; } = new();
    public List<ReplayStepDto> Replay { get; set; } = new();
    public List<CounterExampleDto> CounterExamples { get; set; } = new();
}

public sealed class DepthDatumDto
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public string VerticalDatum { get; set; } = "";
    public string PositiveDirection { get; set; } = "downward increasing depth";
    public string? Note { get; set; }
}

public sealed class SectionDto
{
    public string Id { get; set; } = "";
    public string Code { get; set; } = "";
    public string TopRaw { get; set; } = "";
    public string BaseRaw { get; set; } = "";
    public string DepthUnit { get; set; } = "mbsf";
    public string Recovery { get; set; } = "full";
    public string Source { get; set; } = "";
}

public sealed class BoundaryDto
{
    public string Id { get; set; } = "";
    public string Kind { get; set; } = "contact";
    public string DepthRaw { get; set; } = "";
    public string BetweenSectionTop { get; set; } = "";
    public string BetweenSectionBase { get; set; } = "";
    public string? MissingTopRaw { get; set; }
    public string? MissingBaseRaw { get; set; }
    public string Rationale { get; set; } = "";
    public string Source { get; set; } = "";
}

public sealed class AnchorDto
{
    public string Id { get; set; } = "";
    public string Label { get; set; } = "";
    public string Kind { get; set; } = "radiocarbon";
    public string DepthRaw { get; set; } = "";
    public string DepthUnit { get; set; } = "mbsf";
    /// <summary>年龄原始表示（原样保留，如 "7 250 ± 40 BP"）。</summary>
    public string AgeText { get; set; } = "";
    public string? AgeUnit { get; set; }
    public double? ConventionalBp { get; set; }
    public double? OneSigmaBp { get; set; }
    public string? ReservoirId { get; set; }
    public string? EventId { get; set; }
    public bool InvertedFlag { get; set; }
    /// <summary>并列锚点组：同组内互为替代，候选枚举时择一，全部候选保留。</summary>
    public string? TieGroup { get; set; }
    /// <summary>给定分布（非放射性碳锚点）：gaussian/skew/mixture/empirical。</summary>
    public JsonElement? Distribution { get; set; }
    public string Source { get; set; } = "";
}

public sealed class ReservoirDto
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public string OffsetRaw { get; set; } = "0";
    public string SigmaRaw { get; set; } = "0";
    public string Unit { get; set; } = "yr";
    public string MarineCurve { get; set; } = "MARINE23-FIC";
    public string? Note { get; set; }
}

public sealed class CurveDto
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    /// <summary>[calBP, carbBP] 点对，按 calBP 降序。</summary>
    public List<double[]> Points { get; set; } = new();
}

public sealed class EventBedDto
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public string EventType { get; set; } = "tephra";
    public string DepthRaw { get; set; } = "";
    public string DepthUnit { get; set; } = "mbsf";
    public string? CorrelatedGlobalEvent { get; set; }
    public string Confidence { get; set; } = "high";
    public string? Note { get; set; }
}

public sealed class ExclusionDto
{
    public string AnchorId { get; set; } = "";
    public string Reason { get; set; } = "";
    public string DecidedBy { get; set; } = "";
    public string DecidedAt { get; set; } = "";
    public bool Excluded { get; set; } = true;
}

public sealed class ModelParamsDto
{
    public string Version { get; set; } = "params/initial";
    public int SamplesPerAnchor { get; set; } = 400;
    public int MaxCandidates { get; set; } = 64;
    public double TailThresholdSigma { get; set; } = 3.0;
    /// <summary>倒置层段是否需要显式人工决定才合法。</summary>
    public bool InversionRequiresDecision { get; set; } = true;
    public double DuplicateDepthToleranceMetres { get; set; } = 0.02;
}

public sealed class ReplayStepDto
{
    public string StepId { get; set; } = "";
    public string Title { get; set; } = "";
    public string Narrative { get; set; } = "";
    public string Command { get; set; } = "";
    public Dictionary<string, object>? Args { get; set; }
    public List<string> ExpectStates { get; set; } = new();
    public List<string> ExpectAffected { get; set; } = new();
}

/// <summary>反例档案条目：从基线只改动一个直接来源。</summary>
public sealed class CounterExampleDto
{
    public string VariantId { get; set; } = "";
    public string Title { get; set; } = "";
    public string Hypothesis { get; set; } = "";
    /// <summary>单个直接来源的补丁（path → 新原始值）。</summary>
    public Dictionary<string, object> SingleSourcePatch { get; set; } = new();
    public string? Command { get; set; }
    public Dictionary<string, object>? Args { get; set; }
    public List<string> ExpectContainedTo { get; set; } = new();
    public List<string> MustNotAffect { get; set; } = new();
    public string MachineJudgement { get; set; } = "";
}
