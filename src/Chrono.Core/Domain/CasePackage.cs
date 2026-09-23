namespace Chrono.Core.Domain;

// 不可变材料层：深度基准、分段、边界、放射性年龄分布、储层偏移、事件层相关、排除决定全保留。
public sealed record CasePackage
{
    public required string CaseId { get; init; }
    public required string Title { get; init; }
    public required string Version { get; init; }
    public List<DepthDatum> Datums { get; init; } = new();
    public List<Segment> Segments { get; init; } = new();
    public List<Boundary> Boundaries { get; init; } = new();
    public List<ReservoirOffset> ReservoirOffsets { get; init; } = new();
    public List<AgeAnchor> Anchors { get; init; } = new();
    public List<EventLayer> EventLayers { get; init; } = new();
    public List<ExclusionDecision> Decisions { get; init; } = new();
    public RulesParams Rules { get; init; } = new()
    {
        Version = "rules-001",
        Notes = "初始规则：1cm 视为深度重复，150 yr 视为并列年龄。"
    };
}

public enum DiagnosticSeverity { Error, Warning }

public sealed record Diagnostic
{
    public required string Code { get; init; }
    public required DiagnosticSeverity Severity { get; init; }
    public required string Message { get; init; }
    public required string ObjectId { get; init; }
    public string? Field { get; init; }
    public SourceLocation? Location { get; init; }
    public string? RawFragment { get; init; }
}

public sealed record EffectivePoint
{
    public required string AnchorId { get; init; }
    public required double DepthM { get; init; }
    public required double AgeQ05 { get; init; }
    public required double AgeQ50 { get; init; }
    public required double AgeQ95 { get; init; }
    public bool Mandatory { get; init; }
    public bool IsDuplicateGroup { get; init; }
    public string RunId { get; init; } = "";
}

public sealed record RateInterval
{
    public required string Id { get; init; }            // 稳定显示身份
    public required string RunId { get; init; }
    public required string FromAnchorId { get; init; }
    public required string ToAnchorId { get; init; }
    public required double DepthSpanM { get; init; }
    public required double RateMedianCmPerKyr { get; init; }
    public required double RateLowCmPerKyr { get; init; }
    public required double RateHighCmPerKyr { get; init; }
}

public sealed record BandVertex
{
    public required double DepthM { get; init; }
    public required double AgeQ05 { get; init; }
    public required double AgeQ50 { get; init; }
    public required double AgeQ95 { get; init; }
}

public sealed record CandidateModel
{
    public required string Id { get; init; }            // 稳定身份（内容决定）
    public required string RunId { get; init; }
    public required int Rank { get; init; }
    public required bool Tied { get; init; }            // 等优
    public required List<EffectivePoint> Points { get; init; }
    public required List<RateInterval> Rates { get; init; }
    public required List<BandVertex> Band { get; init; }
    public required List<string> IncludedAnchorIds { get; init; }
    public required List<string> ExcludedAnchorIds { get; init; }
    public required string InputDigest { get; init; }
    public required string RulesVersion { get; init; }
    public required List<string> ParentDependencyIds { get; init; }
}

public sealed record MonotonicRun
{
    public required string Id { get; init; }
    public required string? SegmentIdTop { get; init; }
    public required string? SegmentIdBottom { get; init; }
    public required double DepthTop { get; init; }
    public required double DepthBottom { get; init; }
    public required List<CandidateModel> Candidates { get; init; }
    public required List<Boundary> BreakingBoundaries { get; init; }
}

public enum DeriveOutcome { Ok, ResourceBudgetExhausted, SemanticallyUnsolvable }

public sealed record DeriveResult
{
    public required DeriveOutcome Outcome { get; init; }
    public List<MonotonicRun> Runs { get; init; } = new();
    public List<Diagnostic> Diagnostics { get; init; } = new();
    public string InputDigest { get; init; } = "";
    public string RulesVersion { get; init; } = "";
}
