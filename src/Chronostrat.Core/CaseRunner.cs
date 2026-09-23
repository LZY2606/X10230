using System.Text.Json.Nodes;

namespace Chronostrat.Core;

/// <summary>
/// Replays an offline case package (a narrative of import → revise → derive →
/// decide → freeze steps) against a session. Symbolic references keep the
/// case file stable while event ids are assigned at runtime.
/// </summary>
public static class CaseRunner
{
    public static void Run(Session session, JsonObject caseDoc)
    {
        var named = new Dictionary<string, string>(); // step name -> event id
        foreach (var stepNode in caseDoc["steps"]!.AsArray())
        {
            var step = stepNode!.AsObject();
            string op = step["op"]!.GetValue<string>();
            Event? produced = null;
            switch (op)
            {
                case "import":
                    produced = session.ImportMaterial(step["material"]!.AsObject()).Event;
                    break;
                case "revise-parameters":
                    produced = session.ReviseParameters(new DeriveParameters(
                        step["version"]!.GetValue<int>(),
                        step["mcBudget"]!.GetValue<int>(),
                        step["minValidDraws"]!.GetValue<int>(),
                        step["equivalenceTolerance"]!.GetValue<double>(),
                        step["seed"]!.GetValue<int>()),
                        step["note"]?.GetValue<string>() ?? "");
                    break;
                case "derive":
                    produced = session.Derive().Event;
                    break;
                case "local-recompute":
                {
                    string trigger = ResolveRef(step["trigger"]!.GetValue<string>(), named);
                    var affected = step["affectedSegments"]!.AsArray()
                        .Select(x => x!.GetValue<string>()).ToArray();
                    produced = session.LocalRecompute(trigger, affected).RecomputeEvent;
                    break;
                }
                case "accept":
                case "reject":
                {
                    string candId = ResolveCandidate(session, step["candidate"]!.GetValue<string>());
                    produced = session.Decide(
                        op == "accept" ? EventTypes.CandidateAccepted : EventTypes.CandidateRejected,
                        candId,
                        step["rationale"]!.GetValue<string>(),
                        step["by"]!.GetValue<string>());
                    break;
                }
                case "freeze":
                {
                    string modelSetId = LastModelSetId(session)
                        ?? throw new InvalidOperationException("freeze requires a derived model set");
                    long seq = session.Events().Max(e => e.Seq);
                    if (step["stale"]?.GetValue<bool>() == true) seq = Math.Max(0, seq - 1);
                    // ensure staleness actually loses: if not stale, freeze normally
                    var result = session.Freeze(modelSetId, step["by"]!.GetValue<string>(), seq);
                    produced = result.Event;
                    break;
                }
                default:
                    throw new InvalidOperationException($"unknown case op '{op}'");
            }
            if (step["name"]?.GetValue<string>() is { } name && produced is not null)
                named[name] = produced.Id;
        }
    }

    private static string ResolveRef(string reference, Dictionary<string, string> named)
        => named.TryGetValue(reference, out var id) ? id : reference;

    private static string? LastModelSetId(Session session)
        => session.Events().LastOrDefault(e => e.Type == EventTypes.ModelDerived)
            ?.Payload["id"]?.GetValue<string>();

    private static string ResolveCandidate(Session session, string reference)
    {
        // "retained:0" | "inferior:0" | "any:0" against the latest derived model set
        var derived = session.Events().LastOrDefault(e => e.Type == EventTypes.ModelDerived)
            ?? throw new InvalidOperationException("no derived model set available");
        var parts = reference.Split(':');
        var candidates = derived.Payload["candidates"]!.AsArray()
            .Select(c => c!.AsObject())
            .Where(c => parts[0] == "any" || c["status"]!.GetValue<string>() == parts[0])
            .OrderBy(c => c["id"]!.GetValue<string>(), StringComparer.Ordinal)
            .ToArray();
        return candidates[int.Parse(parts[1])]["id"]!.GetValue<string>();
    }
}
