

using ChronoCore.Domain;

namespace ChronoCore.Engine;

public enum EngineOutcome { Ok = 0, InputIncomplete = 1, BudgetExhausted = 2, SemanticallyUnsolvable = 3 }

public enum ValidationState { Valid = 0, DuplicateDepth = 1, InvertedInterval = 2, MissingDatum = 3, UnconvertedValue = 4 }

public sealed class EffectiveAnchor
{
    public string AnchorId { get; set; } = "";
    public string SourceMaterialId { get; set; } = "";
    public string SourceRef { get; set; } = "";
    public double DepthMbsf { get; set; }
    public string SegmentId { get; set; } = "";
    public bool IsEventAnchor { get; set; }
    public double MeanCalYrBp { get; set; }
    public double Q025 { get; set; }
    public double Q975 { get; set; }
    public double Q16 { get; set; }
    public double Q84 { get; set; }
    public bool LongTail { get; set; }
    public double TailMass { get; set; }
    public string? Note { get; set; }
    public string? ExclusionReason { get; set; }
}

public sealed class RateInterval
{
    public string IntervalId { get; set; } = "";
    public string SegmentId { get; set; } = "";
    public double TopDepth { get; set; }
    public double BaseDepth { get; set; }
    public double ValidDrawFraction { get; set; }
    public double MedianCmPerKa { get; set; }
    public double Q025CmPerKa { get; set; }
    public double Q975CmPerKa { get; set; }
}

public sealed class UncertaintyBandPoint
{
    public string SegmentId { get; set; } = "";
    public double DepthMbsf { get; set; }
    public double Q025YrBp { get; set; }
    public double MedianYrBp { get; set; }
    public double Q975YrBp { get; set; }
}

public sealed class CandidateModel
{
    public string CandidateId { get; set; } = "";
    public string Fingerprint { get; set; } = "";
    public int Rank { get; set; }
    public bool Tied { get; set; }
    public double FitScore { get; set; }
    public double CoveragePenalty { get; set; }
    public List<string> UsedAnchorIds { get; set; } = new();
    public List<string> ExcludedAnchorIds { get; set; } = new();
    public List<RateInterval> Rates { get; set; } = new();
    public List<UncertaintyBandPoint> Band { get; set; } = new();
    public string? InfeasibleReason { get; set; }
}

public sealed class SegmentView
{
    public string SegmentId { get; set; } = "";
    public double TopDepth { get; set; }
    public double BaseDepth { get; set; }
    public BoundaryKind LowerBoundary { get; set; }
    public string Lithology { get; set; } = "";
    public bool Monotonic { get; set; }
}

/// <summary>一次推导的完整结果：输入摘要、参数版本、父级依赖全部记录。</summary>
public sealed class DerivationResult
{
    public string ResultId { get; set; } = "";
    public EngineOutcome Outcome { get; set; }
    public string ParameterSetId { get; set; } = "";
    public string RulesetVersion { get; set; } = "";
    public string InputDigest { get; set; } = "";
    public string? ParentResultId { get; set; }
    public string? ScopeMaterialId { get; set; }
    public List<string> RecomputedSegmentIds { get; set; } = new();
    public List<string> ParentMaterialIds { get; set; } = new();
    public List<DiagnosticEntry> Diagnostics { get; set; } = new();
    public List<SegmentView> Segments { get; set; } = new();
    public List<EffectiveAnchor> Anchors { get; set; } = new();
    public List<CandidateModel> Candidates { get; set; } = new();
    public int DrawsUsed { get; set; }
    public long BudgetTicksUsed { get; set; }
    public bool BudgetExhausted { get; set; }
    public DateTime ComputedAt { get; set; }
}

/// <summary>
/// 稳定显示值：审阅界面每个数值都带稳定身份。身份不依赖渲染顺序，
/// 导出→删除库→再导入后保持不变。
/// </summary>
public sealed record DisplayValue
{
    public string Id { get; init; } = "";
    public string Kind { get; init; } = "";
    public double? Numeric { get; init; }
    public string Text { get; init; } = "";
    public string Unit { get; init; } = "";
    public string Datum { get; init; } = "";
    public double? ValidFrom { get; init; }
    public double? ValidTo { get; init; }
    public string? InvalidReason { get; init; }
    public string SourceMaterialId { get; init; } = "";
    public string? ResultId { get; init; }
}
