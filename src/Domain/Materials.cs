namespace DepthAgeReview.Domain;

/// <summary>深度基准：所有深度坐标都相对于它（如岩芯顶面/core top、海底泥面）。不可变材料。</summary>
public sealed record DepthDatum(string Id, string Name, string Reference, string DepthUnit, string? Note = null);

/// <summary>
/// 岩芯分段。<see cref="TopDepthRaw"/>/&gt;<see cref="BottomDepthRaw"/> 是相对基准的端点。
/// <see cref="Conformable"/>=false 表示顶界为不整合（显式边界），<see cref="GapToTopCm"/> 是与上一段之间的缺口(cm)。
/// </summary>
public sealed record CoreSegment(
    string Id,
    string TopDepthRaw,
    string BottomDepthRaw,
    string DepthUnit,
    bool Conformable,
    double GapToTopCm,
    string Lithology,
    string? Note = null)
{
    public Quantity? Top { get; init; }
    public Quantity? Bottom { get; init; }
}

/// <summary>储层偏移（硬水效应/老碳）。Delta 单位 cal a BP，向年龄方向加。不可变材料。</summary>
public sealed record ReservoirCorrection(string Id, string Name, double DeltaCalAbp, double SigmaCalAbp, string? AppliesToSegmentId = null, string? Note = null);

public enum AgeDistributionKind { Normal, StudentT, Empirical, Point }

/// <summary>
/// 年龄锚点。其年龄是分布而不是一个数：派生层永远保留 <see cref="Distribution"/>，
/// 不允许用均值替代。<see cref="Excluded"/> 由排除决定承载；锚点材料本身不删除。
/// </summary>
public sealed record AgeAnchor(
    string Id,
    string Label,
    string SegmentId,
    string DepthRaw,
    string DepthUnit,
    AgeDistributionKind Kind,
    string AgeUnit,
    double? MeanCalAbp = null,
    double? SigmaCalAbp = null,
    double? Tnu = null,
    double[]? EmpiricalCalAbp = null,
    string? ReservoirCorrectionId = null,
    string DatingMethod = "",
    string? Note = null)
{
    public Quantity? Depth { get; init; }
    public bool Excluded { get; init; }
    public string? ExclusionDecisionId { get; init; }

    public double DepthCm => Depth?.Canonical ?? throw new InvalidOperationException($"锚点 {Id} 深度未规范化");
}

/// <summary>事件层（火山灰/古地磁/同位素事件），可把本孔锚点与外部已知年龄关联。不可变材料。</summary>
public sealed record EventLayer(
    string Id,
    string Label,
    string SegmentId,
    string DepthRaw,
    string DepthUnit,
    string? CorrelatedGlobalEventId,
    double? KnownAgeCalAbp,
    double? KnownAgeSigmaCalAbp,
    string? Note = null)
{
    public Quantity? Depth { get; init; }
}

/// <summary>排除决定（可修改决定层，但历史版本保留）。记录排除了哪条材料、原因与决定序号。</summary>
public sealed record Exclusion(string Id, string TargetAnchorId, string Reason, int DecisionSeq, string? SupersededByEventId = null)
{
    public bool Active => SupersededByEventId is null;
}

/// <summary>一次导入/修订后的全部不可变材料快照（原始表示随对象保留）。</summary>
public sealed record MaterialBundle(
    string Version,
    DepthDatum Datum,
    IReadOnlyList<CoreSegment> Segments,
    IReadOnlyList<AgeAnchor> Anchors,
    IReadOnlyList<ReservoirCorrection> Reservoirs,
    IReadOnlyList<EventLayer> Events,
    IReadOnlyList<Exclusion> Exclusions);
