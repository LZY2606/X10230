using Chrono.Core.Model;

namespace Chrono.Core.Sourcing;

/// <summary>Revisioned parameters of the derivation engine. Parameters are decisions, not material.</summary>
public sealed record DeriveParameters(
    int Version,
    int Samples,
    int DepthGridPoints,
    long BudgetDraws,
    IReadOnlyList<string> Strategies,
    bool ApplyReservoir)
{
    public static readonly DeriveParameters Initial = new(
        1, 4000, 41, 20_000_000L, new[] { "linear", "isotonic" }, true);
}

/// <summary>A human resolution of anchors sharing the exact same depth.</summary>
public sealed record DuplicateResolution(
    double DepthMetres,
    IReadOnlyList<string> AnchorIds,
    string Mode, // alternate | drop
    string? DroppedAnchorId,
    string EventId);

/// <summary>An explicitly added boundary that splits a run (inversion turned into an explicit edge).</summary>
public sealed record AddedBoundary(
    double AtDepthMetres,
    ContactKind Contact,
    string Justification,
    string EventId);

/// <summary>Accepted/declined candidate decision attached to a run.</summary>
public sealed record CandidateDecision(
    string RunId,
    IReadOnlyList<string> AcceptedCandidateIds,
    string Decision, // accept | decline | revise
    string EventId,
    long Seq);

/// <summary>Frozen model: the candidate set and params are immutable afterwards.</summary>
public sealed record FrozenModel(
    string RunId,
    IReadOnlyList<string> CandidateIds,
    int ParametersVersion,
    string CompareToken,
    string EventId,
    long Seq);

/// <summary>A machine-readable finding (principle judgement) emitted as an event.</summary>
public sealed record RuleFinding(
    string Code,
    string Subject,
    bool Holds,
    string Justification,
    IReadOnlyList<string> AffectedObjectIds,
    string EventId,
    long Seq);

/// <summary>Folded state of a branch at one point in time (or at one historical event).</summary>
public sealed class ProjectState
{
    public required string ProjectId { get; init; }
    public required string Name { get; set; }
    public DepthDatum? Datum { get; set; }
    public string CaseId { get; set; } = "";
    public string CaseVersion { get; set; } = "";
    public int MaterialsVersion { get; set; }
    public Dictionary<string, Segment> Segments { get; } = new();
    public Dictionary<string, Anchor> Anchors { get; } = new();
    public Dictionary<string, EventLayer> Events { get; } = new();
    public Dictionary<string, ReservoirOffset> Reservoirs { get; } = new();
    public Dictionary<string, Exclusion> Exclusions { get; } = new();
    public DeriveParameters Parameters { get; set; } = DeriveParameters.Initial;
    public List<DuplicateResolution> DuplicateResolutions { get; } = new();
    public List<AddedBoundary> AddedBoundaries { get; } = new();
    public List<CandidateDecision> CandidateDecisions { get; } = new();
    public List<FrozenModel> Frozen { get; } = new();
    public List<RuleFinding> Findings { get; } = new();
    public List<ChronoEvent> AppliedEvents { get; } = new();
    public List<Model.Anchor> CompareAnchors { get; } = new();
    public string? ForkedFrom { get; set; }

    public IEnumerable<Anchor> ActiveAnchors()
    {
        foreach (var a in Anchors.Values.OrderBy(a => a.Id, StringComparer.Ordinal))
        {
            var ex = Exclusions.TryGetValue(a.Id, out var e) && e.Active;
            yield return ex ? a with { Excluded = true } : a;
        }
    }

    public bool IsRunFrozen(string runId) => Frozen.Any(f => f.RunId == runId);
}
