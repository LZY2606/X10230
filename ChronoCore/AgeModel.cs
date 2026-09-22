namespace ChronoCore;

public sealed class QuantileBand
{
    public double P05 { get; set; }
    public double P50 { get; set; }
    public double P95 { get; set; }
    public double Mean { get; set; }
    public double Std { get; set; }
}

public sealed class GridPoint
{
    public string Id { get; set; } = "";        // stable within candidate, e.g. GP-0007
    public double DepthM { get; set; }
    public QuantileBand Age { get; set; } = new();
}

public sealed class RateInterval
{
    public string SegmentId { get; set; } = "";
    public double DepthFromM { get; set; }
    public double DepthToM { get; set; }
    public QuantileBand YearsPerMetre { get; set; } = new();
    public QuantileBand CmPerYear { get; set; } = new();
}

public static class CandidateStatus
{
    public const string Proposed = "proposed";
    public const string Frozen = "frozen";
    public const string Superseded = "superseded";
}

/// <summary>One defensible depth-age candidate. Multiple tied candidates must all be retained.</summary>
public sealed class AgeModelCandidate
{
    public string Id { get; set; } = "";                    // content hash, stable across runs
    public int ParameterVersion { get; set; }
    public string ParentMaterialHashes { get; set; } = "";
    public List<string> UsedAnchorIds { get; set; } = new();
    public List<string> SkippedAnchorIds { get; set; } = new();
    public List<GridPoint> Grid { get; set; } = new();
    public List<RateInterval> Rates { get; set; } = new();
    public List<Judgment> Judgments { get; set; } = new();
    public double Score { get; set; }
    public string TieKey { get; set; } = "";
    public string Status { get; set; } = CandidateStatus.Proposed;
    public string FrozenByEventSeq { get; set; } = "";
    public int LineageGeneration { get; set; } = 1;
    public string LineageParentId { get; set; } = "";
}

public sealed class BuildResult
{
    public int Generation { get; set; }
    public string Trigger { get; set; } = "";
    public string Outcome { get; set; } = "ok";             // ok | BUDGET_EXHAUSTED | SEMANTIC_UNRESOLVABLE
    public List<AgeModelCandidate> Candidates { get; set; } = new();
    public List<string> RecomputedAnchorIds { get; set; } = new();
    public List<string> PreservedCandidateIds { get; set; } = new();
    public List<Diagnostic> Diagnostics { get; set; } = new();
    public List<Judgment> GlobalJudgments { get; set; } = new();
}
