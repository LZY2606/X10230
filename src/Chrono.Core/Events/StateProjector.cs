using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using Chrono.Core.Materials;
using Chrono.Core.Model;

namespace Chrono.Core.Events;

/// <summary>
/// Replays immutable events onto state. Going back to any old event restores the then-current
/// dependency versions and visible conclusions — the latest computation never overwrites history.
/// </summary>
public sealed class StateProjector
{
    private static readonly JsonSerializerOptions O = new(JsonSerializerDefaults.Web);

    public ProjectState Project(IEnumerable<EventRecord> events, string branch, long? upToSeq = null)
    {
        var state = new ProjectState();
        var branchEvents = events
            .Where(e => e.Branch == branch || e.Branch == "main")
            .OrderBy(e => e.Branch == "main" ? 0 : 1)
            .ThenBy(e => e.Seq)
            .ToList();

        foreach (var ev in branchEvents)
        {
            if (upToSeq is not null && ev.Branch == branch && ev.Seq > upToSeq) break;
            Apply(ev, state);
        }
        return state;
    }

    public Dictionary<string, ProjectState> ProjectAllBranches(IEnumerable<EventRecord> events)
    {
        var list = events.ToList();
        var branches = list.Select(e => e.Branch).Distinct().ToList();
        return branches.ToDictionary(b => b, b => Project(list, b));
    }

    private static void Apply(EventRecord ev, ProjectState state)
    {
        using var doc = JsonDocument.Parse(ev.PayloadJson);
        var p = doc.RootElement;
        switch (ev.Type)
        {
            case EventTypes.ProjectCreated:
                state.ProjectId = p.GetProperty("projectId").GetString()!;
                state.CoreId = p.GetProperty("coreId").GetString()!;
                break;

            case EventTypes.MaterialImported:
            {
                var mat = MaterialSerialization.Deserialize(p);
                if (mat is not null) state.Materials[mat.Id] = mat;
                if (mat is ExclusionDecisionMaterial ex) state.Exclusions.Add(ex);
                break;
            }
            case EventTypes.SourceAmended:
            {
                if (p.TryGetProperty("supersedesMaterialId", out var sup) && sup.ValueKind == JsonValueKind.String)
                {
                    var oldId = sup.GetString()!;
                    if (state.Materials.TryGetValue(oldId, out var old))
                        state.Materials[oldId] = old with { Superseded = true,
                            SupersededBy = p.GetProperty("replacement").GetProperty("id").GetString() };
                }
                var mat = MaterialSerialization.Deserialize(p.GetProperty("replacement"));
                if (mat is not null) state.Materials[mat.Id] = mat;
                break;
            }
            case EventTypes.ExclusionDecided:
            {
                var target = p.GetProperty("targetMaterialId").GetString()!;
                var reason = p.GetProperty("reason").GetString()!;
                MarkExcluded(state, target, reason);
                var decision = new ExclusionDecisionMaterial(
                    p.GetProperty("decisionId").GetString()!, $"event:{ev.Id}", ev.Type, 0,
                    target, reason, ev.Actor);
                state.Exclusions.Add(decision);
                break;
            }
            case EventTypes.ParameterRevised:
            case EventTypes.RuleVersionSet:
                state.Rules = new RuleSet(
                    p.GetProperty("version").GetString()!,
                    p.GetProperty("interpolation").GetString()!,
                    p.GetProperty("drawCount").GetInt32(),
                    p.GetProperty("rngSeed").GetUInt64(),
                    p.GetProperty("longTailProbabilityThreshold").GetDouble(),
                    p.GetProperty("maxCandidates").GetInt32(),
                    p.GetProperty("minSedRateCmPerYear").GetDouble(),
                    p.GetProperty("maxSedRateCmPerYear").GetDouble());
                break;
            case EventTypes.CorrelationChosen:
            {
                var group = p.GetProperty("correlationGroup").GetString()!;
                var chosen = p.GetProperty("chosenEventTieId").GetString()!;
                state.ChosenCorrelations[group] = chosen;
                // non-chosen members are excluded from modeling (but kept as materials)
                foreach (var m in state.Materials.Values.OfType<EventTieMaterial>()
                             .Where(e => (e.CorrelationGroup ?? "") == group && e.Id != chosen))
                    state.Materials[m.Id] = m with { Excluded = true, ExclusionReason = "conflicting correlation choice" };
                foreach (var m in state.Materials.Values.OfType<EventTieMaterial>()
                             .Where(e => e.Id == chosen))
                    state.Materials[m.Id] = m with { Excluded = false, ExclusionReason = null };
                break;
            }
            case EventTypes.CandidateRejected:
                state.RejectedCandidates.Add(p.GetProperty("candidateId").GetString()!);
                break;
            case EventTypes.ModelFrozen:
                state.FrozenDerivationId = p.GetProperty("derivationId").GetString();
                state.FrozenCandidateId = p.TryGetProperty("candidateId", out var ci) ? ci.GetString() : null;
                break;
        }
    }

    private static void MarkExcluded(ProjectState state, string target, string reason)
    {
        if (!state.Materials.TryGetValue(target, out var m)) return;
        state.Materials[target] = m switch
        {
            RadiometricDateMaterial r => r with { Excluded = true, ExclusionReason = reason },
            EventTieMaterial e => e with { Excluded = true, ExclusionReason = reason },
            _ => m
        };
    }
}
