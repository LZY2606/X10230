using System.Text.Json.Serialization;

namespace ChronoCore.Domain;

/// <summary>
/// 不可变材料的统一容器。深度基准、分段缺口、放射性年龄分布、储层偏移、
/// 事件层相关以及所有排除决定均在此保留；任何一层都不做物理删除。
/// </summary>
public sealed class Material
{
    public string Id { get; set; } = "";
    public MaterialKind Kind { get; set; }
    public string SourceRef { get; set; } = "";
    public string SourceFragment { get; set; } = "";
    public int SourceLine { get; set; }
    public DateTime ImportedAt { get; set; }

    // 放射性年龄
    public string? SampleCode { get; set; }
    public string? LabCode { get; set; }
    public DepthDatum DepthDatum { get; set; } = DepthDatum.Mbsf;
    public Measurement? DepthRaw { get; set; }
    public Measurement? AgeRaw { get; set; }
    public TimeScale AgeScale { get; set; } = TimeScale.Bp1950;
    public Measurement? OneSigmaRaw { get; set; }
    public string? Distribution { get; set; }
    public double? ShapeK { get; set; }
    public double? ScaleTheta { get; set; }
    public double? MinClamp { get; set; }
    public double? TailWeight { get; set; }
    public string? ReservoirId { get; set; }
    public string? MaterialType { get; set; }

    // 分段 / 边界
    public Measurement? TopDepthRaw { get; set; }
    public Measurement? BaseDepthRaw { get; set; }
    public BoundaryKind BoundaryAtBase { get; set; }
    public string? Lithology { get; set; }
    public string? SegmentCode { get; set; }
    public bool OverturnedSection { get; set; }

    // 储层偏移
    public string? ReservoirName { get; set; }
    public Measurement? DeltaR { get; set; }
    public Measurement? DeltaRSigma { get; set; }
    public Measurement? ValidFromAge { get; set; }
    public Measurement? ValidToAge { get; set; }

    // 事件层
    public string? EventName { get; set; }
    public Measurement? EventDepthRaw { get; set; }
    public Measurement? EventAgeRaw { get; set; }
    public Measurement? EventAgeSigmaRaw { get; set; }
    public string? CorrelationBasis { get; set; }

    public bool Excluded { get; set; }
    public string? ExclusionReason { get; set; }
    public string? ExclusionEventId { get; set; }

    public Material ShallowCopy() => (Material)MemberwiseClone();
}

/// <summary>可修改决定。决定只追加、可撤回；不可变材料永不就地覆盖。</summary>
public sealed class Decision
{
    public string Id { get; set; } = "";
    public DecisionKind Kind { get; set; }
    public DecisionStatus Status { get; set; } = DecisionStatus.Proposed;
    public string? TargetMaterialId { get; set; }
    public string ParameterSetId { get; set; } = "";
    public string Reason { get; set; } = "";
    public string PayloadJson { get; set; } = "{}";
    public string? SupersedesDecisionId { get; set; }
    public string? ConflictsWithDecisionId { get; set; }
    public string EventId { get; set; } = "";
    public DateTime CreatedAt { get; set; }
}

/// <summary>参数版本。规则以显式版本号参与输入摘要。</summary>
public sealed class ParameterSet
{
    public string Id { get; set; } = "";
    public int Version { get; set; }
    public string RulesetVersion { get; set; } = "";
    public int MonteCarloDraws { get; set; } = 4000;
    public long CpuBudgetTicks { get; set; } = TimeSpan.FromSeconds(20).Ticks;
    public double TiedScoreEpsilon { get; set; } = 1e-9;
    public bool ApplyReservoirToMarine { get; set; } = true;
    public bool KeepReworkedTail { get; set; } = true;
    public DateTime CreatedAt { get; set; }
    public string? Note { get; set; }
}

/// <summary>
/// 追加式事件台账。机器可读：结构化类型 + 负载 JSON + 输入摘要 + 父级依赖。
/// 回到任意旧事件即恢复当时的依赖版本与可见结论。
/// </summary>
public sealed class LedgerEvent
{
    public long Seq { get; set; }
    public string Id { get; set; } = "";
    public string Type { get; set; } = "";
    public string BranchId { get; set; } = "main";
    public string? ParentEventId { get; set; }
    public string ParameterSetId { get; set; } = "";
    public string InputDigest { get; set; } = "";
    public string PayloadJson { get; set; } = "{}";
    public string? ResultRef { get; set; }
    public DateTime CreatedAt { get; set; }
    public string Actor { get; set; } = "reviewer";
}

public sealed class Branch
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public string? FrozenAtEventId { get; set; }
    public string? FreezeDigest { get; set; }
    public bool Frozen => FrozenAtEventId is not null;
    public DateTime CreatedAt { get; set; }
}
