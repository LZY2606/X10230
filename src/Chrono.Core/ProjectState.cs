namespace Chrono.Core;

/// <summary>Projection of one branch up to a given event (historical views are real projections).</summary>
public sealed record BranchState
{
    public string BranchId { get; init; } = "";
    public string? Name { get; init; }
    public string? CaseId { get; init; }
    public MaterialSet? Materials { get; init; }
    public string? MaterialRevisionId => Materials?.RevisionId;
    public ParameterSet Parameters { get; init; } = ParameterSet.V1();
    public IReadOnlyList<DerivationResult> Derivations { get; init; } = Array.Empty<DerivationResult>();
    public IReadOnlyList<ChronoEvent> Events { get; init; } = Array.Empty<ChronoEvent>();
    public IReadOnlyList<DecisionView> Decisions { get; init; } = Array.Empty<DecisionView>();
    public FrozenView? Frozen { get; init; }
    public int Version => Events.Count;

    public DerivationResult? LatestDerivation => Derivations.Count == 0 ? null : Derivations[^1];
}

public sealed record DecisionView(string CandidateId, string Action, string Note,
    string EventId, string? SupersedesEventId, bool Conflicts);
public sealed record FrozenView(string CandidateId, string EventId, string Note);

public static class Projector
{
    public static BranchState Project(string branchId, IEnumerable<ChronoEvent> stream)
    {
        var ordered = stream.OrderBy(e => e.Seq).ToList();
        string? name = null, caseId = null;
        MaterialSet? materials = null;
        var pars = ParameterSet.V1();
        var derivations = new List<DerivationResult>();
        var decisions = new List<DecisionView>();
        FrozenView? frozen = null;

        foreach (var ev in ordered)
        {
            switch (ev.Payload)
            {
                case ProjectCreatedPayload pc: name = pc.Name; caseId = pc.CaseId; break;
                case MaterialsPayload mp: materials = mp.Materials; break;
                case SourceReplacedPayload sr: materials = sr.Materials; break;
                case ParameterPayload pp: pars = pp.Parameters; break;
                case DerivedPayload dp: derivations.Add(dp.Result); break;
                case DecisionPayload dec:
                    decisions.Add(new DecisionView(dec.TargetCandidateId, dec.Action, dec.Note,
                        ev.EventId, dec.SupersedesEventId, dec.Conflicts));
                    break;
                case FrozenPayload fz: frozen = new FrozenView(fz.CandidateId, ev.EventId, fz.Note); break;
            }
        }
        return new BranchState
        {
            BranchId = branchId, Name = name, CaseId = caseId, Materials = materials,
            Parameters = pars, Derivations = derivations, Events = ordered, Decisions = decisions,
            Frozen = frozen
        };
    }
}
