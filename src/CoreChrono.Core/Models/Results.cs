namespace CoreChrono.Core.Models;

/// <summary>Structured status of one structural/statistical check; replayable, not just a log line.</summary>
public sealed record CheckResult
{
    public string Code { get; init; } = "";        // DEPTH_DUPLICATE, INVERSION, TIE, LONG_TAIL ...
    public string Status { get; init; } = "";      // pass|violation|warning|boundary
    public string SegmentId { get; init; } = "";
    public List<string> AffectedIds { get; init; } = new();
    public string Detail { get; init; } = "";
    public string InputSnippet { get; init; } = ""; // verbatim imported fragment driving the check
}

public sealed record RateInterval
{
    public string SegmentId { get; init; } = "";
    public double DepthFrom { get; init; }
    public double DepthTo { get; init; }
    public double RateMedian { get; init; }        // yr per metre
    public double RateP05 { get; init; }
    public double RateP95 { get; init; }
    public bool CrossesBoundary { get; init; }
}

public sealed record UncertaintyBandPoint
{
    public double Depth { get; init; }
    public double AgeP05 { get; init; }
    public double AgeMedian { get; init; }
    public double AgeP95 { get; init; }
    public bool InContinuousSection { get; init; }
}

/// <summary>A complete defensible age-depth model candidate with its full posterior, not a mean.</summary>
public sealed record CandidateModel
{
    public string Id { get; init; } = "";
    public string Method { get; init; } = "";      // linear | pchip | isotonic
    public string ReservoirScenario { get; init; } = "applied";
    public string TiePolicy { get; init; } = "";   // how tied/parallel anchors resolved
    public double Score { get; init; }
    public bool EquallyOptimal { get; init; }
    public bool Adopted { get; set; }
    public List<string> AnchorOrdering { get; init; } = new();
    public List<UncertaintyBandPoint> Band { get; init; } = new();
    public List<RateInterval> Rates { get; init; } = new();
    public Dictionary<string, string> Basis { get; init; } = new();
}

public sealed record DerivationResult
{
    public bool SemanticallyValid { get; init; }
    public List<Diagnostic> Diagnostics { get; init; } = new();
    public List<CheckResult> Checks { get; init; } = new();
    public List<CandidateModel> Candidates { get; init; } = new();
    public List<string> OrderedAnchorIds { get; init; } = new();
    public HashSet<string> ExcludedAnchorIds { get; init; } = new();
}
