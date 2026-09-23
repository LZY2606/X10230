namespace DepthAge.Core.Models;

public enum SegmentBoundary
{
    Conformable,
    Gap,
    Unconformity,
    Fault,
    Inversion
}

/// <summary>连续（不断层）段：年龄单调约束只在段内成立。</summary>
public sealed record ModelSegment
{
    public required string Id { get; init; }
    public required string CoreId { get; init; }
    public required double TopDepthM { get; init; }
    public required double BottomDepthM { get; init; }
    public required SegmentBoundary UpperBoundary { get; init; }
    public required SegmentBoundary LowerBoundary { get; init; }
    public required string BoundaryReason { get; init; }
    public List<string> SectionIds { get; init; } = new();
    public List<string> AnchorIds { get; init; } = new();
    public bool Inverted { get; init; }
    public bool Solvable { get; init; } = true;
    public List<string> Diagnostics { get; init; } = new();
}

public sealed record RateInterval
{
    public required string SegmentId { get; init; }
    public required double FromDepthM { get; init; }
    public required double ToDepthM { get; init; }
    public required string FromAnchorId { get; init; }
    public required string ToAnchorId { get; init; }
    public required double MedianCmPerKyr { get; init; }
    public required double LowCmPerKyr { get; init; }   // 2.5%
    public required double HighCmPerKyr { get; init; }  // 97.5%
    public required bool AgeDirectionOk { get; init; } // 段内下覆更老
    public required double EffectiveTopDepthM { get; init; }
    public required double EffectiveBottomDepthM { get; init; }
    public string? InvalidationReason { get; init; }
}

public sealed record BandPoint(double DepthM, double AgeLowCalYrBP, double AgeMedianCalYrBP, double AgeHighCalYrBP);

public sealed record UncertaintyBand
{
    public required string SegmentId { get; init; }
    public required List<BandPoint> Points { get; init; }
    public required double EffectiveTopDepthM { get; init; }
    public required double EffectiveBottomDepthM { get; init; }
}

/// <summary>一套可辩护候选。并列候选全部保留，不做人为唯一化。</summary>
public sealed record CandidateModel
{
    public required string Id { get; init; }
    public required string RankKey { get; init; }        // 等价输入得到稳定顺序
    public required string Method { get; init; }         // Linear | PAVA
    public required int Rank { get; init; }
    public required bool TiedWithBest { get; init; }
    public required double Score { get; init; }          // 越小越优
    public required List<string> UsedAnchorIds { get; init; }
    public required List<string> DroppedAnchorIds { get; init; } // 段内违反单调时的最小舍弃集
    public required List<RateInterval> Rates { get; init; }
    public required List<UncertaintyBand> Bands { get; init; }
    public required List<string> SegmentIds { get; init; }
    public required List<QuantilePoint> Predictions { get; init; } // 调试/复核用
    public List<string> JudgmentIds { get; init; } = new();
    public bool Accepted { get; init; }
    public string? DecisionId { get; init; }
    public Dictionary<string, string> Attributes { get; init; } = new();
}

/// <summary>计算结果记录：输入摘要、参数版本、父级依赖三件齐全。</summary>
public sealed record ComputationRecord
{
    public required string Id { get; init; }
    public required long AtSeq { get; init; }
    public required string RuleVersion { get; init; }
    public required string RuleBookId { get; init; }
    public required string InputDigest { get; init; }
    public required string? ParentComputationId { get; init; }
    public required bool LocalRecompute { get; init; }
    public required List<string> ChangedSourceIds { get; init; }
    public required List<string> ImpactedObjectIds { get; init; }
    public required List<ModelSegment> Segments { get; init; }
    public required List<CandidateModel> Candidates { get; init; }
    public required List<Judgment> Judgments { get; init; }
    public required List<Diagnostic> Diagnostics { get; init; }
    public required string Status { get; init; } // Ok | SemanticImpasse | BudgetExhausted
    public required DateTimeOffset ComputedAt { get; init; }
    public bool Frozen { get; init; }
    public string? FreezeId { get; init; }
}
