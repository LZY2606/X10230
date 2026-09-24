using System.Text.Json;
using AgeDepth.Domain;
using AgeDepth.Store;

namespace AgeDepth.CasePack;

/// <summary>Replays the offline domain case pack ("岩芯年代尺") into a fresh store.</summary>
public static class CasePackLoader
{
    private static readonly JsonSerializerOptions J = new() { PropertyNameCaseInsensitive = true };

    public static void Replay(AppService svc, string path)
    {
        var doc = JsonDocument.Parse(File.ReadAllText(path));
        foreach (var step in doc.RootElement.GetProperty("steps").EnumerateArray())
        {
            var op = step.GetProperty("op").GetString()!;
            switch (op)
            {
                case "import":
                    svc.ImportMaterial(
                        step.GetProperty("kind").GetString()!,
                        JsonSerializer.Deserialize<JsonElement>(step.GetProperty("material").GetRawText()),
                        step.TryGetProperty("revises", out var rv) ? rv.GetString() : null);
                    break;
                case "params":
                    svc.DefineParams(step.GetProperty("version").GetString()!,
                        step.GetProperty("toleranceYr").GetDouble(),
                        step.GetProperty("note").GetString()!);
                    break;
                case "exclusion":
                    svc.RecordExclusion(JsonSerializer.Deserialize<ExclusionRecord>(
                        step.GetProperty("record").GetRawText(), J)!);
                    break;
                case "decision":
                    svc.RecordDecision(step.GetProperty("targetId").GetString()!,
                        step.GetProperty("action").GetString()!,
                        step.GetProperty("rationale").GetString()!,
                        step.GetProperty("decidedBy").GetString()!);
                    break;
                case "freeze":
                    svc.Freeze(step.GetProperty("by").GetString()!, svc.ModelSetVersion);
                    break;
                case "freeze-stale":
                    // Simulates a concurrent freeze arriving with a stale model-set version.
                    svc.Freeze(step.GetProperty("by").GetString()!, svc.ModelSetVersion - 1);
                    break;
                case "anchor-impact":
                    svc.AddAnchorWithImpact(JsonSerializer.Deserialize<AgeAnchor>(
                        step.GetProperty("material").GetRawText(), J)!);
                    break;
            }
        }
    }
}
