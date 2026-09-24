using Chrono.Core.Models;

namespace Chrono.Core.Domain;

public enum CandidateFamily { LinearInterpolation, ConstantRate, PiecewiseBacon }

public sealed record RateInterval(double TopM, double BottomM, double RateLo, double RateHi, bool CrossesBarrier, string Basis);

public sealed record ModelCandidate(
    string Id, string FamilyLabel, CandidateFamily Family,
    IReadOnlyList<string> AnchorIds,
    double Score, bool EquallyOptimal, bool Frozen,
    string? FrozenAtEventId, string? SupersededBy,
    IReadOnlyList<RateInterval> Rates,
    IReadOnlyList<(double depthM, double ageLo, double ageMed, double ageHi)> Band,
    IReadOnlyList<Judgment> Judgments,
    string ParamsVersion, IReadOnlyList<string> ParentCandidateIds,
    string DerivedAtEventId, int Revision,
    bool Accepted = true, string? RejectReason = null)
{
    public ModelCandidate WithFrozen(string eventId) => this with { Frozen = true, FrozenAtEventId = eventId };
    public ModelCandidate Rejected(string reason) => this with { Accepted = false, RejectReason = reason };
}

public sealed record BranchInfo(string Id, string Name, string CreatedAtEventId, string? ParentBranchId, bool IsScratch, long ForkSeq = 0);

public sealed record ProbeRecord(
    string Id, string ProbeKind, string BaselineDigest, string SingleSourceChanged,
    IReadOnlyList<string> ExpectedScope, IReadOnlyList<string> ActualAffected,
    bool ScopeCorrect, bool Adopted, string? NotAdoptedReason,
    IReadOnlyList<Judgment> Judgments, string EventId);

public sealed record PendingConflict(string Id, string DecisionId, string OptionA, string OptionB, string Note);

/// <summary>Projected state. Rebuilt purely by replaying StoredEvents; reading
/// any prefix of a branch restores that historical dependency graph &amp; conclusion.</summary>
public sealed class AppState
{
    public string ProjectId = "";
    public string Name = "";
    public string RuleVersion = ParameterSet.Current;
    public ParameterSet Parameters = new(ParameterSet.Current, 0.05, 1, 200, 120, 256, new Dictionary<string,string>());
    public DepthDatum? Datum;
    public readonly Dictionary<string, CoreSegment> Segments = new();
    public readonly Dictionary<string, AgeAnchor> Anchors = new();
    public readonly Dictionary<string, ReservoirCorrection> Reservoirs = new();
    public readonly Dictionary<string, EventLayer> Events = new();
    public readonly Dictionary<string, ExclusionDecision> Exclusions = new();
    public readonly Dictionary<string, ModelCandidate> Candidates = new();
    public readonly Dictionary<string, BranchInfo> Branches = new();
    public readonly List<ProbeRecord> Probes = new();
    public readonly List<PendingConflict> PendingConflicts = new();
    public readonly List<Diagnostic> Diagnostics = new();
    public readonly List<string> EventOrder = new();
    public string BranchId = "main";
    public string? FrozenCandidateId;
    public string? FrozenAtEventId;
    public long LastSeq;
    public string? LastEventId;
    public string? FrozenSinceEvent;

    public IEnumerable<AgeAnchor> OrderedUsableAnchors() =>
        Anchors.Values.Where(a => a.Usable && a.Depth.IsConvertible)
            .OrderBy(a => a.Depth.NormValue!.Value);
}
