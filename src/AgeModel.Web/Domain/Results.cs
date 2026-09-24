using System.Text.Json.Serialization;

namespace AgeModel.Domain;

public enum OutcomeKind
{
    /// <summary>语义无解：例如断层段内年龄无法单调且无法排除。</summary>
    SemanticNoSolution,
    /// <summary>资源预算耗尽：合法候选数量超预算。</summary>
    BudgetExhausted,
    /// <summary>格式损坏（JSON/结构无法解析）。</summary>
    CorruptFormat,
    Ok
}

/// <summary>速率区间：cm/ka（归一化后）与原始单位双保留。</summary>
public sealed record RateBand(double DepthCm, double Low, double Median, double High, string Unit);

/// <summary>深度-年龄网格上的不确定度带（联合采样，不是逐点独立区间）。</summary>
public sealed record UncertaintyBand(double DepthCm, double Q05, double Q50, double Q95);

/// <summary>一个可辩护的深度-年龄模型候选。</summary>
public sealed record ModelCandidate
{
    public required string CandidateId { get; init; }
    public required int Rank { get; init; }
    public required bool IsEquivalentBest { get; init; }
    public required double Score { get; init; }
    /// <summary>采纳的锚点序列（层段内、未排除、满足单调）。</summary>
    public required IReadOnlyList<string> AnchorIdsInOrder { get; init; }
    /// <summary>并列/被舍弃但合法的锚点（保留，供审阅）。</summary>
    public required IReadOnlyList<string> TiedAnchorIds { get; init; }
    public required IReadOnlyList<UncertaintyBand> Bands { get; init; }
    public required IReadOnlyList<RateBand> Rates { get; init; }
    public required string RuleVersionId { get; init; }
    public required string SourceId { get; init; }
    /// <summary>输入摘要（内容寻址），用于父级依赖与局部重算。</summary>
    public required string InputDigest { get; init; }
    public required IReadOnlyList<string> ParentDependencyDigests { get; init; }
}

/// <summary>单个连续层段的推导结果。</summary>
public sealed record SectionResult
{
    public required string SectionId { get; init; }
    public required IReadOnlyList<ModelCandidate> Candidates { get; init; }
    public required OutcomeKind Outcome { get; init; }
    public required IReadOnlyList<Diagnostic> Diagnostics { get; init; }
    public bool Frozen { get; init; }
    public string? FrozenCandidateId { get; init; }
}

/// <summary>机器可读判定事件（判断依据，而非普通日志）。</summary>
public sealed record JudgmentEvent
{
    public required string EventId { get; init; }
    public required string Type { get; init; }
    public required string At { get; init; }
    public required string SubjectPath { get; init; }
    public required string Status { get; init; } // accepted | rejected | inconclusive | diagnostic
    public required string RuleVersionId { get; init; }
    public required string SourceId { get; init; }
    public string? DetailJson { get; init; }
    public IReadOnlyList<string> AffectedObjectIds { get; init; } = [];
}

/// <summary>整个岩芯的推导输出。</summary>
public sealed record DerivationOutput(
    string DerivationId, string SourceId, string RuleVersionId,
    IReadOnlyList<ContiguousSection> Sections,
    IReadOnlyList<SectionResult> SectionResults,
    IReadOnlyList<JudgmentEvent> Events,
    IReadOnlyList<Diagnostic> Diagnostics,
    OutcomeKind OverallOutcome,
    bool BudgetHit);

public sealed record FrozenModel(string CandidateId, string SectionId, string FrozenAtEventId, string FrozenBy, string Note);
