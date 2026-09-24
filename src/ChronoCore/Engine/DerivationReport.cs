namespace ChronoCore.Engine;

/// <summary>三种不同结果必须类型可区分。</summary>
public enum DeriveOutcome { Derived, SemanticInfeasible, ResourceBudgetExhausted }

public sealed class Diagnostic
{
    public string Code { get; set; }
    public string Message { get; set; }
    public string Location { get; set; }   // 例：/materials/ages/2/depth
    public string Severity { get; set; }   // error|warning
}

public sealed class RateInterval
{
    public string SegmentId { get; set; }
    public string AnchorFrom { get; set; }
    public string AnchorTo { get; set; }
    public double DepthFromM { get; set; }
    public double DepthToM { get; set; }
    public double RateLowCmPerKa { get; set; }
    public double RateHighCmPerKa { get; set; }
}

public sealed class BandPoint
{
    public double DepthM { get; set; }
    public double AgeLowA { get; set; }
    public double AgeHighA { get; set; }
}

public sealed class CandidateCurve
{
    public string CandidateId { get; set; }
    public string Family { get; set; }           // 建模族（lin-linear 等）
    public double Score { get; set; }
    public bool Tied { get; set; }               // 与最优并列
    public List<RateInterval> Rates { get; set; } = new();
    public List<BandPoint> Band { get; set; } = new();
    public List<string> AnchorIds { get; set; } = new();
}

public sealed class InversionHit
{
    public string SegmentId { get; set; }
    public string UpperAnchor { get; set; }
    public string LowerAnchor { get; set; }
    public double UpperDepthM { get; set; }
    public double LowerDepthM { get; set; }
    public double OverlapA { get; set; }
}

public sealed class SegmentReport
{
    public string SegmentId { get; set; }
    public string Kind { get; set; }             // section|gap
    public double TopM { get; set; }
    public double BottomM { get; set; }
    public List<string> AnchorIds { get; set; } = new();
    public bool Monotonic { get; set; }
    public List<InversionHit> Inversions { get; set; } = new();
}

public sealed class DerivationReport
{
    public string DerivationId { get; set; }
    public string ParamVersion { get; set; }
    public string InputHash { get; set; }
    public DeriveOutcome Outcome { get; set; }
    public List<Diagnostic> Diagnostics { get; set; } = new();
    public List<CandidateCurve> Candidates { get; set; } = new();
    public List<SegmentReport> Segments { get; set; } = new();
    public List<string> DuplicateDepthGroups { get; set; } = new();
    public bool TiesPresent { get; set; }
    public int Enumerated { get; set; }
    public int BudgetLimit { get; set; }
}
