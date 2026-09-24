using System.Text.Json.Nodes;

namespace Chrono.Domain;

public enum EventType
{
    BundleImported = 0,
    MaterialAccepted = 1,
    MaterialExcluded = 2,
    ParamsRevised = 3,
    SourceRevised = 4,             // 只改动一个直接来源 -> 局部重算
    ModelsComputed = 5,
    CandidateAdopted = 6,
    CandidateRejected = 7,
    ModelFrozen = 8,
    NewAnchorCompared = 9,
    HumanDecision = 10,            // 互相冲突的人工决定
    ConflictResolved = 11,
    ReplayCheckpoint = 12,
    BundleExported = 13,
    CounterExample = 14,           // 反例：只改一个直接来源后的影响范围
    DiagnosticRaised = 15
}

/// <summary>结构化状态与可重放证据；不只是日志。</summary>
public sealed class ChronoEvent
{
    public required long Seq { get; init; }
    public required string EventId { get; init; }        // 稳定身份 ev-xxxx
    public required string BranchId { get; init; }
    public required EventType Type { get; init; }
    public required string OccurredAt { get; init; }     // 仅展示；排序只认 Seq
    public required string RuleVersion { get; init; }
    public required string ParamsVersion { get; init; }
    public required string InputDigest { get; init; }    // 输入摘要（SHA256）
    public required IReadOnlyList<string> Parents { get; init; }   // 父级依赖事件 id
    public required IReadOnlyList<string> AffectedObjectIds { get; init; } // 受影响对象
    public required JsonNode Payload { get; init; }      // 结构化判定（机器可读）
    public required OutcomeKind Outcome { get; init; }
    public List<Diagnostic> Diagnostics { get; init; } = new();
    public string? Actor { get; init; }                  // system / reviewer / replay
}

/// <summary>深度-年龄模型候选：等优候选全部保留，不得只留一个。</summary>
public sealed record CandidateModel
{
    public required string CandidateId { get; init; }    // 稳定身份
    public required string BranchId { get; init; }
    public required string EventId { get; init; }        // 产生它的 ModelsComputed
    public required int Rank { get; init; }              // 稳定排序（同分按 canonical 摘要）
    public required double Score { get; init; }
    public required bool EquallyOptimal { get; init; }   // 是否属于等优集合
    public required bool Monotonic { get; init; }        // 仅在不断层段内检查
    public required List<string> UsedAnchorIds { get; init; }
    public required List<SegmentRate> SegmentRates { get; init; }
    public required List<AgeBandPoint> UncertaintyBand { get; init; }
    public required Dictionary<string, string> Provenance { get; init; } // anchor/segment/event 来源关系
    public required string Status { get; init; }         // computed | adopted | rejected | frozen | superseded
}

/// <summary>一个不断层段内的沉积速率区间（m/ka，从分布得到 P05–P95）。</summary>
public sealed record SegmentRate(
    string SegmentId,
    double DepthTopM, double DepthBottomM,
    double MedianMPerKa, double P05MPerKa, double P95MPerKa,
    double MedianAgeTopBP, double P05AgeTopBP, double P95AgeTopBP,
    double MedianAgeBottomBP, double P05AgeBottomBP, double P95AgeBottomBP,
    string Unit, string AgeFrame);

/// <summary>不确定度带上的采样点：每个显示值都带稳定身份。</summary>
public sealed record AgeBandPoint(
    string PointId, string SegmentId, double DepthM,
    double MedianAgeBP, double P05AgeBP, double P95AgeBP, string AgeFrame);

/// <summary>岩芯纵剖分段（显式缺口/不整合边界）。</summary>
public sealed record CoreSegmentView(
    string SegmentId, string? Label, double TopM, double BottomM,
    bool Continuous, string BoundaryType, bool Inverted,
    string DepthUnit, string? Basis, List<DisplayAnchor> Anchors,
    List<DisplayEvent> Events, List<string> Diagnostics);

public sealed record DisplayAnchor(
    string AnchorId, int Version, double DepthM, string DepthBasis,
    string Frame, string Unit, string Shape, double MedianAge, double P05, double P95,
    string? TieGroup, string? ReservoirMethod, string Status, string SourceRef, int SourceLine,
    string StableValueId);

public sealed record DisplayEvent(
    string EventBedId, int Version, double DepthM, string EventCode,
    List<string> CorrelatesTo, string SourceRef);
