using Chrono.Core.Events;
using Chrono.Core.Materials;

namespace Chrono.Storage.Workflow;

public sealed record ProjectedState(
    MaterialProject? Materials,
    ChronoParameters Parameters,
    IReadOnlyList<SourceExclusion> DecisionExclusions,
    IReadOnlyList<string> FrozenCandidateIds,
    IReadOnlyList<ProjectEvent> Events);

public static class MaterialProjector
{
    public static ProjectedState Fold(IEnumerable<ProjectEvent> events)
    {
        MaterialProject? materials = null;
        var parameters = ChronoParameters.Initial;
        var decisionExclusions = new List<SourceExclusion>();
        var frozen = new List<string>();
        var list = events.OrderBy(e => e.Sequence).ToList();

        foreach (var e in list)
        {
            switch (e.Kind)
            {
                case EventKind.MaterialsCommitted:
                    var snap = e.Payload<MaterialsSnapshotEnvelope>();
                    materials = snap.Project;
                    break;
                case EventKind.ParametersRevised:
                    var revised = e.Payload<ParametersEnvelope>();
                    parameters = revised.Parameters;
                    break;
                case EventKind.DecisionRecorded:
                    var decision = e.Payload<DecisionPayload>();
                    decisionExclusions.Add(new SourceExclusion(
                        decision.DecisionId, decision.TargetId,
                        decision.Action == DecisionAction.ExcludeAnchor ? ExclusionKind.HumanReview : ExclusionKind.HumanReview,
                        decision.Rationale, $"event:{e.Id}", true, decision.Rationale));
                    if (decision.Action == DecisionAction.ReinstateAnchor)
                        decisionExclusions.RemoveAll(x => x.TargetAnchorId == decision.TargetId);
                    break;
                case EventKind.ModelFrozen:
                    var f = e.Payload<FrozenModelPayload>();
                    if (!frozen.Contains(f.FrozenCandidateId)) frozen.Add(f.FrozenCandidateId);
                    break;
            }
        }

        return new ProjectedState(materials, parameters, decisionExclusions, frozen, list);
    }

    public static ProjectedState At(IReadOnlyList<ProjectEvent> branchEvents, string eventId)
    {
        var upTo = new List<ProjectEvent>();
        foreach (var e in branchEvents.OrderBy(x => x.Sequence))
        {
            upTo.Add(e);
            if (e.Id == eventId) break;
        }
        return Fold(upTo);
    }
}

public sealed record MaterialsSnapshotEnvelope(MaterialProject Project);
public sealed record ParametersEnvelope(ChronoParameters Parameters);
