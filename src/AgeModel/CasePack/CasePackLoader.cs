using System.Text.Json.Nodes;
using AgeModel.Store;

namespace AgeModel.CasePack;

/// <summary>Replays a case-pack document into the event store. A case pack is the
/// domain narrative: initial import, a parameter revision, a local source change,
/// conflicting manual decisions and a freeze, all as replayable steps.</summary>
public static class CasePackLoader
{
    public static int Load(JsonObject doc, EventStore store)
    {
        if (doc["format"]?.GetValue<string>() != "agemodel-casepack/1")
            throw new InvalidDataException("not an agemodel-casepack/1 document");
        var count = 0;
        foreach (var step in doc["steps"]!.AsArray())
        {
            var s = step!;
            var op = s["op"]!.GetValue<string>();
            switch (op)
            {
                case "import":
                    foreach (var m in s["materials"]!.AsArray())
                    { store.Append($"mat-{m!["id"]!.GetValue<string>()}", "material.imported", new { material = m }); count++; }
                    break;
                case "revise":
                    store.Append($"rev-{s["parameters"]!["version"]!.GetValue<int>()}", "parameters.revised",
                        JsonNode.Parse(s["parameters"]!.ToJsonString())!.AsObject());
                    count++;
                    break;
                case "source-change":
                    store.Append($"chg-{s["material"]!["id"]!.GetValue<string>()}-{count}", "source.changed",
                        new { material = s["material"]!, note = s["note"]?.GetValue<string>() });
                    count++;
                    break;
                case "decision":
                    store.Append(s["id"]!.GetValue<string>(), "decision.manual", new
                    {
                        id = s["id"]!.GetValue<string>(),
                        action = s["action"]!.GetValue<string>(),
                        targetId = s["targetId"]!.GetValue<string>(),
                        reason = s["reason"]!.GetValue<string>(),
                        decidedBy = s["decidedBy"]!.GetValue<string>()
                    });
                    count++;
                    break;
                case "freeze":
                {
                    var expected = s["expectedVersion"]!.GetValue<long>();
                    var result = store.Freeze(Projector.ModelId, expected,
                        new { note = s["note"]?.GetValue<string>() ?? "", by = s["by"]?.GetValue<string>() ?? "case-pack" });
                    store.Append($"freeze-v{result.Version}", "model.frozen",
                        new { modelId = Projector.ModelId, version = result.Version, status = result.Status.ToString(), result.Message });
                    count++;
                    break;
                }
                default:
                    throw new InvalidDataException($"unknown case-pack op '{op}'");
            }
        }
        return count;
    }
}
