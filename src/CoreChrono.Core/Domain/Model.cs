namespace CoreChrono.Core.Domain;

public sealed record Project(string Id, string Name, string CreatedAt);

/// <summary>派生锚点（来自放射性年龄或具相关年龄的事件层），应用储层偏移后的分布。</summary>
public sealed record AnchorPoint(
    string Id, string SourceMaterialId, string Label,
    DepthValue Depth, AgeDistribution Age,
    double ReservoirOffsetAppliedCalYr,
    bool ExcludedByDecision, string? ExclusionReason,
    string SourceHash);

public sealed record ModelParams(
    int Draws = 400,
    int MaxSubsets = 64,
    int MaxCandidatesKept = 16,
    double MinRateYrPerM = 1.0,
    double MaxRateYrPerM = 100_000.0,
    int Seed = 20260919,
    string Version = "params/v1");

public sealed record SegmentModel(
    string Id,
    double TopM, double BottomM,
    IReadOnlyList<string> AnchorIds,
    double RateMedianYrPerM, double RateP05YrPerM, double RateP95YrPerM,
    IReadOnlyList<double> BandDepthsM,
    IReadOnlyList<double> BandP05, IReadOnlyList<double> BandP50, IReadOnlyList<double> BandP95,
    int AcceptedDraws, int RejectedDraws, bool ReusedFromPrevious,
    string InputDigest);

public sealed record CandidateModel(
    string Id, int Index,
    IReadOnlyList<string> BoundaryGapIds,
    IReadOnlyList<SegmentModel> Segments,
    IReadOnlyList<string> ExcludedByDecisionAnchorIds,
    IReadOnlyList<string> AutoExcludedAnchorIds,
    IReadOnlyList<string> RejectedReasonBySegment,
    double Score, bool EqualOptimal, bool Accepted,
    string? RejectedReason,
    string InputDigest, string ParamsVersion,
    IReadOnlyList<string> ParentEventIds,
    long DeriveSeq, string Status, long? FrozenSeq);

/// <summary>机器可读取的规则判定事件依据。</summary>
public sealed record JudgmentRecord(string Rule, string Statement, IReadOnlyList<string> AppliedToObjectIds, string Severity);

public sealed record ChronoEvent(
    long Seq, string Id, string ProjectId, string Type,
    string Payload, string? ParentEventId, string CreatedAt);

public abstract record DeriveOutcome;

public sealed record DeriveSuccess(
    string InputDigest, string ParamsVersion,
    IReadOnlyList<CandidateModel> Accepted,
    IReadOnlyList<CandidateModel> Rejected,
    IReadOnlyList<Diagnostic> Diagnostics,
    IReadOnlyList<JudgmentRecord> Judgments,
    IReadOnlyList<string> RecomputedSegmentKeys,
    IReadOnlyList<string> ReusedSegmentKeys) : DeriveOutcome;

/// <summary>资源预算耗尽（边界组合数超过上限）：区别于格式损坏与语义无解。</summary>
public sealed record BudgetExhausted(int RequiredSubsets, int MaxSubsets, string Detail) : DeriveOutcome;

/// <summary>语义无解：不存在任何满足约束的候选（如全岩芯年龄倒置）。</summary>
public sealed record SemanticallyUnsolvable(string Reason, IReadOnlyList<Diagnostic> Diagnostics) : DeriveOutcome;
