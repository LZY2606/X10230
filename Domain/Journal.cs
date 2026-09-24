using System.Text.Json.Serialization;

namespace CoreChrono.Domain;

public enum EventType
{
    ProjectCreated, MaterialImported, ExclusionRecorded, ParameterSetIssued,
    ModelDerived, DecisionRecorded, ModelFrozen, FreezeRejected,
    BranchCreated, ProbeRecorded, BaselineCaptured, RuleJudged
}

public enum DecisionAction { Accept, Reject, ParameterAdjust }

public sealed class JournalEvent
{
    public long Seq { get; init; }
    public string EventId { get; init; } = "";
    public string ProjectId { get; init; } = "";
    public string Branch { get; init; } = "main";
    public EventType Type { get; init; }
    public Stamp Stamp { get; init; } = new("system", DateTimeOffset.UnixEpoch);
    public string Summary { get; init; } = "";
    public string InputHash { get; init; } = "";
    public string ParameterVersion { get; init; } = "";
    public List<string> ParentEvents { get; init; } = new();
    public List<string> ParentNodes { get; init; } = new();
    public JsonElement Payload { get; init; }
}

public sealed class DerivedNode
{
    public string NodeId { get; set; } = "";
    public string Branch { get; set; } = "";
    public string Kind { get; set; } = "";
    public string Fingerprint { get; set; } = "";
    public string ParameterVersion { get; set; } = "";
    public List<string> ParentNodes { get; set; } = new();
    public string? SourceEventId { get; set; }
    public JsonElement Body { get; set; }
    public bool Invalidated { get; set; }
}

public sealed class ModelCandidate
{
    public string CandidateId { get; set; } = "";
    public string UnitCode { get; set; } = "";
    public string ParameterVersion { get; set; } = "";
    public List<string> AnchorIds { get; set; } = new();
    public double Score { get; set; }
    public bool Monotonic { get; set; }
    public bool TieGroup { get; set; }
    public bool Adopted { get; set; }
    public bool Superseded { get; set; }
    public RateInterval[] RateIntervals { get; set; } = Array.Empty<RateInterval>();
    public UncertaintyBand Band { get; set; } = new();
    public string Fingerprint { get; set; } = "";
}

public sealed record RateInterval(double TopM, double BottomM, double MedianCmKa, double LowCmKa, double HighCmKa);

public sealed record BandPoint(double DepthM, double MedianAgeBP, double LowAgeBP, double HighAgeBP, bool Extrapolated);

public sealed class UncertaintyBand
{
    public List<BandPoint> Points { get; set; } = new();
}

public sealed class DecisionRecord
{
    public string DecisionId { get; set; } = "";
    public string Branch { get; set; } = "main";
    public DecisionAction Action { get; set; }
    public string? CandidateId { get; set; }
    public string? ParameterVersion { get; set; }
    public string Note { get; set; } = "";
    public bool Conflicting { get; set; }
    public string? SupersedesDecisionId { get; set; }
}

public sealed class FreezeRecord
{
    public string FreezeId { get; set; } = "";
    public string Branch { get; set; } = "main";
    public string CandidateId { get; set; } = "";
    public string ParameterVersion { get; set; } = "";
    public string EventId { get; set; } = "";
    public string InputHash { get; set; } = "";
    public string SnapshotNodeId { get; set; } = "";
    public bool Conflict { get; set; }
    public string? ConflictReason { get; set; }
}

public sealed class ProbeRecord
{
    public string ProbeId { get; set; } = "";
    public string Branch { get; set; } = "";
    public string Scenario { get; set; } = "";
    public string DirectSourceChanged { get; set; } = "";
    public ProbeStatus Status { get; set; }
    public List<Diagnosis> Diagnostics { get; set; } = new();
    public List<string> AffectedNodes { get; set; } = new();
    public List<string> UnchangedNodes { get; set; } = new();
    public List<string> AdoptedCandidateIds { get; set; } = new();
    public List<string> RejectedCandidateIds { get; set; } = new();
    public string InputFragmentHash { get; set; } = "";
    public string? InputFragment { get; set; }
    public string VerdictEventId { get; set; } = "";
}

public sealed class RuleJudgement
{
    public string RuleId { get; set; } = "";
    public bool Holds { get; set; }
    public string Basis { get; set; } = "";
    public Dictionary<string, object?> Evidence { get; set; } = new();
    public List<string> AffectedObjectIds { get; set; } = new();
}
