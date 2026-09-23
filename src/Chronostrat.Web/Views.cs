using System.Text.Json.Nodes;
using Chronostrat.Core;

namespace Chronostrat.Web;

public static class MaterialsView
{
    public static JsonObject ToJson(MaterialsSnapshot s)
    {
        return new JsonObject
        {
            ["datum"] = s.Datum is null ? null : new JsonObject
            {
                ["id"] = s.Datum.Id, ["name"] = s.Datum.Name,
                ["referenceLevel"] = s.Datum.ReferenceLevel, ["unit"] = s.Datum.Unit,
                ["orientation"] = s.Datum.Orientation
            },
            ["segments"] = new JsonArray(s.Segments.Select(x => (JsonNode)new JsonObject
            {
                ["id"] = x.Id, ["topDepth"] = x.TopDepth, ["baseDepth"] = x.BaseDepth,
                ["unit"] = x.Unit, ["datumId"] = x.DatumId
            }).ToArray()),
            ["boundaries"] = new JsonArray(s.Boundaries.Select(x => (JsonNode)new JsonObject
            {
                ["id"] = x.Id, ["kind"] = x.Kind, ["atDepth"] = x.AtDepth,
                ["unit"] = x.Unit, ["note"] = x.Note
            }).ToArray()),
            ["anchors"] = new JsonArray(s.Anchors.Select(a => (JsonNode)new JsonObject
            {
                ["id"] = a.Id, ["depth"] = a.Depth, ["depthUnit"] = a.DepthUnit,
                ["datumId"] = a.DatumId, ["source"] = a.Source,
                ["reservoirOffsetId"] = a.ReservoirOffsetId,
                ["excluded"] = s.IsExcluded(a.Id),
                ["age"] = DistJson(a.Age)
            }).ToArray()),
            ["reservoirOffsets"] = new JsonArray(s.ReservoirOffsets.Select(r => (JsonNode)new JsonObject
            {
                ["id"] = r.Id, ["offset"] = DistJson(r.Offset)
            }).ToArray()),
            ["eventLayers"] = new JsonArray(s.EventLayers.Select(l => (JsonNode)new JsonObject
            {
                ["id"] = l.Id, ["name"] = l.Name, ["depth"] = l.Depth, ["depthUnit"] = l.DepthUnit,
                ["confidence"] = l.Confidence, ["excluded"] = s.IsExcluded(l.Id),
                ["correlatedAge"] = l.CorrelatedAge is null ? null : DistJson(l.CorrelatedAge)
            }).ToArray()),
            ["exclusions"] = new JsonArray(s.Exclusions.Select(x => (JsonNode)new JsonObject
            {
                ["id"] = x.Id, ["targetId"] = x.TargetId, ["reason"] = x.Reason,
                ["decidedBy"] = x.DecidedBy, ["decidedAt"] = x.DecidedAt
            }).ToArray()),
            ["diagnostics"] = new JsonArray(s.ImportDiagnostics.Select(d => (JsonNode)new JsonObject
            {
                ["code"] = d.Code, ["message"] = d.Message, ["position"] = d.Position, ["severity"] = d.Severity
            }).ToArray())
        };
    }

    private static JsonObject DistJson(AgeDistribution d)
    {
        var o = new JsonObject { ["kind"] = d.Kind, ["unit"] = d.Unit };
        if (d.Mean is { } m) o["mean"] = m;
        if (d.Sd is { } sd) o["sd"] = sd;
        if (d.Min is { } mn) o["min"] = mn;
        if (d.Max is { } mx) o["max"] = mx;
        if (d.Samples is { } sa) o["samples"] = new JsonArray(sa.Select(JsonValue.Create).ToArray());
        return o;
    }
}

public static class CaseView
{
    public static JsonObject Info()
    {
        try
        {
            string path = Path.Combine(AppContext.BaseDirectory, "cases", "core-age-scale-case.json");
            var doc = JsonNode.Parse(File.ReadAllText(path))!.AsObject();
            return new JsonObject
            {
                ["id"] = doc["id"]!.GetValue<string>(),
                ["title"] = doc["title"]!.GetValue<string>(),
                ["narrative"] = doc["narrative"]!.GetValue<string>()
            };
        }
        catch
        {
            return new JsonObject { ["id"] = "unknown", ["title"] = "", ["narrative"] = "" };
        }
    }
}
