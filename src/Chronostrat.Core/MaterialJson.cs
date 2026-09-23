using System.Text.Json.Nodes;

namespace Chronostrat.Core;

/// <summary>Codec between material records and their raw JSON representation.</summary>
public static class MaterialJson
{
    public static AgeDistribution ParseDist(JsonObject o) => new(
        o["kind"]!.GetValue<string>(),
        o["unit"]!.GetValue<string>(),
        o["mean"]?.GetValue<double>(),
        o["sd"]?.GetValue<double>(),
        o["min"]?.GetValue<double>(),
        o["max"]?.GetValue<double>(),
        o["samples"] is JsonArray sa ? sa.Select(x => x!.GetValue<double>()).ToArray() : null);

    public static JsonObject DistToJson(AgeDistribution d)
    {
        var o = new JsonObject
        {
            ["kind"] = d.Kind,
            ["unit"] = d.Unit
        };
        if (d.Mean is { } m) o["mean"] = m;
        if (d.Sd is { } s) o["sd"] = s;
        if (d.Min is { } mn) o["min"] = mn;
        if (d.Max is { } mx) o["max"] = mx;
        if (d.Samples is { } sa) o["samples"] = new JsonArray(sa.Select(JsonValue.Create).ToArray());
        return o;
    }

    public static (string Kind, object Material) Parse(JsonObject o) => o["kind"]!.GetValue<string>() switch
    {
        MaterialKinds.DepthDatum => (MaterialKinds.DepthDatum, new DepthDatum(
            o["id"]!.GetValue<string>(), o["name"]!.GetValue<string>(),
            o["referenceLevel"]!.GetValue<string>(), o["unit"]!.GetValue<string>(),
            o["orientation"]!.GetValue<string>())),
        MaterialKinds.Segment => (MaterialKinds.Segment, new Segment(
            o["id"]!.GetValue<string>(), o["topDepth"]!.GetValue<double>(),
            o["baseDepth"]!.GetValue<double>(), o["unit"]!.GetValue<string>(),
            o["datumId"]!.GetValue<string>())),
        MaterialKinds.Boundary => (MaterialKinds.Boundary, new Boundary(
            o["id"]!.GetValue<string>(), o["boundaryKind"]!.GetValue<string>(),
            o["atDepth"]!.GetValue<double>(), o["unit"]!.GetValue<string>(),
            o["datumId"]!.GetValue<string>(), o["note"]?.GetValue<string>())),
        MaterialKinds.AgeAnchor => (MaterialKinds.AgeAnchor, new AgeAnchor(
            o["id"]!.GetValue<string>(), o["depth"]!.GetValue<double>(),
            o["depthUnit"]!.GetValue<string>(), o["datumId"]!.GetValue<string>(),
            ParseDist(o["age"]!.AsObject()), o["reservoirOffsetId"]?.GetValue<string>(),
            o["source"]!.GetValue<string>())),
        MaterialKinds.ReservoirOffset => (MaterialKinds.ReservoirOffset, ParseOffset(o)),
        MaterialKinds.EventLayer => (MaterialKinds.EventLayer, new EventLayer(
            o["id"]!.GetValue<string>(), o["name"]!.GetValue<string>(),
            o["depth"]!.GetValue<double>(), o["depthUnit"]!.GetValue<string>(),
            o["datumId"]!.GetValue<string>(),
            o["correlatedAge"] is JsonObject ca ? ParseDist(ca) : null,
            o["confidence"]!.GetValue<string>())),
        MaterialKinds.ExclusionDecision => (MaterialKinds.ExclusionDecision, new ExclusionDecision(
            o["id"]!.GetValue<string>(), o["targetId"]!.GetValue<string>(),
            o["reason"]!.GetValue<string>(), o["decidedBy"]!.GetValue<string>(),
            o["decidedAt"]!.GetValue<string>())),
        var other => throw new FormatException($"unknown material kind '{other}'")
    };

    public static ReservoirOffset ParseOffset(JsonObject o) => new(
        o["id"]!.GetValue<string>(), ParseDist(o["offset"]!.AsObject()));
}
