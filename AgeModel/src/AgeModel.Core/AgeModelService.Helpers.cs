using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace AgeModel.Core;

public sealed partial class AgeModelService
{
    private EventEnvelope CreateEvent(
        string projectId,
        string type,
        string branch,
        IReadOnlyList<string> parentEventIds,
        string ruleVersion,
        string payload,
        DateTimeOffset now,
        string? parentBranch = null)
    {
        var sequence = store.NextSequence(projectId);
        var hashInput = string.Join("\n", projectId, sequence, type, branch, ruleVersion, payload, string.Join(",", parentEventIds));
        var eventId = "evt-" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(hashInput))).ToLowerInvariant()[..16];
        var inputHash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(payload))).ToLowerInvariant();
        return new EventEnvelope(eventId, projectId, sequence, now.ToString("O"), type, branch,
            parentEventIds, ruleVersion, inputHash, payload);
    }

    private static CommandResult Fail(string code, string message) =>
        new(false, code, null, null, null, new[] { new Diagnostic(code, "error", "$", null, message) });

    private static ProjectState Apply(ProjectState state, EventEnvelope evt, IReadOnlyList<EventEnvelope> log)
    {
        return evt.EventType switch
        {
            "parameters.revised" => ApplyParameters(state, evt, log),
            "source.patched" => ApplySourcePatch(state, evt, log),
            "decision.recorded" => ApplyDecision(state, evt, log),
            "correlation.decided" => ApplyCorrelation(state, evt, log),
            "model.frozen" => ApplyFreeze(state, evt, log),
            "trial.anchor-proposed" => ApplyTrialAnchor(state, evt, log),
            "counterexample.archived" => state with { EventsLog = AppendLog(state, log, evt), BaselineArchiveEventId = evt.EventId },
            _ => state with { EventsLog = AppendLog(state, log, evt) }
        };
    }

    private static List<EventEnvelope> AppendLog(ProjectState state, IReadOnlyList<EventEnvelope> log, EventEnvelope evt)
    {
        var next = new List<EventEnvelope>(log) { evt };
        return next;
    }

    private static ProjectState ApplyParameters(ProjectState state, EventEnvelope evt, IReadOnlyList<EventEnvelope> log)
    {
        var parameters = JsonSerializer.Deserialize<RuleParameters>(evt.PayloadJson, ChronologyJson.Options)!;
        return state with { Parameters = parameters, EventsLog = AppendLog(state, log, evt) };
    }

    private static ProjectState ApplySourcePatch(ProjectState state, EventEnvelope evt, IReadOnlyList<EventEnvelope> log)
    {
        using var doc = JsonDocument.Parse(evt.PayloadJson);
        var sourceId = doc.RootElement.GetProperty("sourceId").GetString()!;
        var replacement = JsonSerializer.Deserialize<AgeAnchor>(doc.RootElement.GetProperty("replacement"), ChronologyJson.Options)!;
        var anchors = new Dictionary<string, AgeAnchor>(state.Anchors) { [sourceId] = replacement with { Id = sourceId } };
        var package = state.Package with { Anchors = ReplaceAtSameId(state.Package.Anchors, anchors[sourceId]) };
        return state with { Anchors = anchors, Package = package, EventsLog = AppendLog(state, log, evt) };
    }

    private static ProjectState ApplyDecision(ProjectState state, EventEnvelope evt, IReadOnlyList<EventEnvelope> log)
    {
        var decision = JsonSerializer.Deserialize<ExclusionDecision>(evt.PayloadJson, ChronologyJson.Options)!;
        var exclusions = new Dictionary<string, ExclusionDecision>(state.Exclusions) { [decision.Id] = decision };
        return state with { Exclusions = exclusions, EventsLog = AppendLog(state, log, evt) };
    }

    private static ProjectState ApplyCorrelation(ProjectState state, EventEnvelope evt, IReadOnlyList<EventEnvelope> log)
    {
        using var doc = JsonDocument.Parse(evt.PayloadJson);
        var eventId = doc.RootElement.GetProperty("eventId").GetString()!;
        var status = doc.RootElement.GetProperty("status").GetString()!;
        var correlations = new Dictionary<string, string>(state.AcceptedCorrelations);
        var layer = state.Events[eventId];
        if (status == "accepted") correlations[layer.CorrelationId] = layer.Id;
        else correlations.Remove(layer.CorrelationId);
        return state with { AcceptedCorrelations = correlations, EventsLog = AppendLog(state, log, evt) };
    }

    private static ProjectState ApplyFreeze(ProjectState state, EventEnvelope evt, IReadOnlyList<EventEnvelope> log)
    {
        using var doc = JsonDocument.Parse(evt.PayloadJson);
        var candidateId = doc.RootElement.GetProperty("candidateId").GetString()!;
        return state with
        {
            FrozenEventId = evt.EventId,
            FrozenCandidateId = candidateId,
            EventsLog = AppendLog(state, log, evt)
        };
    }

    private static ProjectState ApplyTrialAnchor(ProjectState state, EventEnvelope evt, IReadOnlyList<EventEnvelope> log)
    {
        var anchor = JsonSerializer.Deserialize<AgeAnchor>(evt.PayloadJson, ChronologyJson.Options)!;
        var anchors = new Dictionary<string, AgeAnchor>(state.Anchors) { [anchor.Id] = anchor };
        var packageAnchors = state.Package.Anchors.Concat(new[] { anchor }).ToArray();
        return state with
        {
            Anchors = anchors,
            Package = state.Package with { Anchors = packageAnchors },
            Branch = evt.Branch,
            EventsLog = AppendLog(state, log, evt)
        };
    }

    private static ProjectState PatchStateForProbe(ProjectState state, CounterProbe probe, string fragment, out List<Diagnostic> diagnostics)
    {
        diagnostics = new List<Diagnostic>();
        var anchors = new Dictionary<string, AgeAnchor>(state.Anchors);
        var anchor = JsonSerializer.Deserialize<AgeAnchor>(fragment, ChronologyJson.Options)! with { Id = probe.ChangedDirectSourceId };
        anchors[probe.ChangedDirectSourceId] = anchor;
        var package = state.Package with { Anchors = ReplaceAtSameId(state.Package.Anchors, anchor) };
        return state with { Anchors = anchors, Package = package };
    }

    private static IReadOnlyList<AgeAnchor> ReplaceAtSameId(IReadOnlyList<AgeAnchor> anchors, AgeAnchor replacement)
    {
        return anchors.Select(a => a.Id == replacement.Id ? replacement : a).ToArray();
    }

    private static IReadOnlyList<string> ComputeAffectedIds(EngineResult before, EngineResult after, string directSourceId)
    {
        var affected = new HashSet<string>(StringComparer.Ordinal) { directSourceId };
        var beforeRates = before.Candidates.SelectMany(c => c.Rates).GroupBy(r => r.Id).ToDictionary(g => g.Key, g => g.First());
        var afterRates = after.Candidates.SelectMany(c => c.Rates).GroupBy(r => r.Id).ToDictionary(g => g.Key, g => g.First());
        foreach (var id in beforeRates.Keys.Union(afterRates.Keys))
        {
            if (!beforeRates.TryGetValue(id, out var oldRate) || !afterRates.TryGetValue(id, out var newRate) ||
                Math.Abs(oldRate.MedianRateCmPerKyr - newRate.MedianRateCmPerKyr) > 1e-9 ||
                Math.Abs(oldRate.LowRateCmPerKyr - newRate.LowRateCmPerKyr) > 1e-9 ||
                Math.Abs(oldRate.HighRateCmPerKyr - newRate.HighRateCmPerKyr) > 1e-9)
            {
                affected.Add(id);
                if (oldRate is not null)
                {
                    affected.Add(oldRate.FromAnchorId);
                    affected.Add(oldRate.ToAnchorId);
                }
                if (newRate is not null)
                {
                    affected.Add(newRate.FromAnchorId);
                    affected.Add(newRate.ToAnchorId);
                }
            }
        }
        return affected.Order(StringComparer.Ordinal).ToArray();
    }

    private static List<EventEnvelope> Ancestors(IReadOnlyList<EventEnvelope> all, EventEnvelope target)
    {
        var byId = all.ToDictionary(e => e.EventId);
        var selected = new HashSet<string>();
        var queue = new Queue<string>();
        queue.Enqueue(target.EventId);
        while (queue.Count > 0)
        {
            var id = queue.Dequeue();
            if (!selected.Add(id) || !byId.TryGetValue(id, out var evt)) continue;
            foreach (var parent in evt.ParentEventIds) queue.Enqueue(parent);
        }
        return all.Where(e => selected.Contains(e.EventId)).OrderBy(e => e.Sequence).ToList();
    }

    private static HashSet<string> BranchAncestors(IReadOnlyList<EventEnvelope> all, string branch)
    {
        var branchHeads = all.Where(e => e.Branch == branch).OrderByDescending(e => e.Sequence).Take(1).ToList();
        if (branchHeads.Count == 0) return new HashSet<string>();
        return Ancestors(all, branchHeads[0]).Select(e => e.EventId).ToHashSet();
    }
}
