namespace AgeDepthStudio.Domain;

/// <summary>三种必须可区分的失败/成功结果类别。</summary>
public enum OutcomeKind { Ok, BudgetExhausted, FormatCorrupt, SemanticUnsolvable }

public enum GapKind { Hiatus, Unconformity }

public enum DepthDirection { DownIncreases }

public enum DecisionAction { ExcludeAnchor, ReinstateAnchor, PreferCandidate, ContestCandidate }

public enum FreezeState { Draft, Frozen }

/// <summary>带位置的诊断。位置指向输入材料中的具体字段。</summary>
public sealed record Diagnostic(string Code, string Severity, string Message, string? MaterialId, string? JsonPointer);

/// <summary>不可变材料：岩芯项目（深度基准在此声明）。</summary>
public sealed record CoreProject(
    string ProjectId,
    string Name,
    string Datum,                 // 例如 "MDRT(m, 补心海拔 31.5m)"
    DepthDirection DepthDirection,
    string DepthUnit,             // "m"
    string AgeUnit,               // "ka BP"
    string CalendarReference      // "BP1950"
);

/// <summary>不可变材料：连续分段。段内年龄必须单调；段间由缺口显式断开。</summary>
public sealed record CoreSegment(
    string SegmentId,
    string ProjectId,
    string TopDepthRaw,
    string BaseDepthRaw,
    string Lithology);

/// <summary>不可变材料：分段缺口（不整合是显式边界）。</summary>
public sealed record SegmentGap(
    string GapId,
    string ProjectId,
    string AboveSegmentId,
    string BelowSegmentId,
    GapKind Kind,
    string? Note);

/// <summary>不可变材料：放射性年龄锚点。分布以样本保存，绝不折叠为均值。</summary>
public sealed record AgeAnchor(
    string AnchorId,
    string SegmentId,
    string DepthRaw,
    string LabCode,
    string Material,
    double[] Samples,             // 单位 ka BP，原始测定分布
    string? Note);

/// <summary>不可变材料：储层偏移（老碳/储层效应），派生层才做改正。</summary>
public sealed record ReservoirOffset(
    string OffsetId,
    string AnchorId,
    double OffsetKa,
    double SigmaKa,
    string Basis);

/// <summary>不可变材料：事件层（火山灰/洪泛等），用于跨段相关。</summary>
public sealed record EventLayer(
    string LayerId,
    string ProjectId,
    string SegmentId,
    string DepthRaw,
    string LayerType,
    string? CorrelatesTo,
    string? Note);

/// <summary>可修改决定（全部保留，含被推翻者）。</summary>
public sealed record Decision(
    string DecisionId,
    string TargetId,
    DecisionAction Action,
    string Rationale,
    string Author,
    string At);

/// <summary>规则参数版本。</summary>
public sealed record RuleParams(
    string ParamsId,
    int Version,
    int EnumerationBudget,
    double AgeInflation,
    double LongTailRatio,
    string Note);

/// <summary>锚点派生量（规范化只发生在派生层）。</summary>
public sealed record AnchorDerived(
    string AnchorId,
    double DepthM,                // 已含储层/基准换算后的深度（米）
    double MuKa,
    double SigmaKa,
    double Q01, double Q05, double Q50, double Q95, double Q99,
    bool LongTail);

public sealed record CurveKnot(double DepthM, double Q01, double Q05, double Q50, double Q95, double Q99);

public sealed record RateInterval(double FromDepthM, double ToDepthM, double RateMin, double RateMax, string Unit);

/// <summary>段内一个可辩护候选模型。</summary>
public sealed record CandidateModel(
    string CandidateId,
    string SegmentId,
    string[] AnchorIds,           // 该候选采纳的锚点（并列锚点每组取一）
    double Score,
    CurveKnot[] Knots,
    RateInterval[] Rates);

public sealed record SegmentResult(
    string SegmentId,
    OutcomeKind Outcome,
    IReadOnlyList<CandidateModel> Candidates,
    IReadOnlyList<Diagnostic> Diagnostics);

public sealed record ModelResult(
    string ModelId,
    string ParamsId,
    string InputFingerprint,
    IReadOnlyList<SegmentResult> Segments,
    OutcomeKind OverallOutcome);
