using System.Text.Json.Nodes;

namespace GeoChron.Domain;

/// <summary>
/// Immutable source material. Raw representation is preserved verbatim;
/// only the derivation layer parses/normalizes it. Identity is content-addressed.
/// </summary>
public sealed class RawItem
{
    public string Id { get; }
    public string? Lineage { get; }
    public string? Supersedes { get; }
    public string Type { get; }
    public JsonObject Raw { get; }
    public string Source { get; }
    public int Order { get; }

    public RawItem(string type, JsonObject raw, string source, int order, string? lineage = null, string? supersedes = null)
    {
        Type = type;
        Raw = raw;
        Source = source;
        Order = order;
        Lineage = lineage;
        Supersedes = supersedes;
        Id = CanonicalJson.HashHex(Basis(), "mat_");
    }

    JsonObject Basis() => new()
    {
        ["type"] = Type,
        ["raw"] = Raw?.DeepClone(),
        ["source"] = Source,
        ["order"] = Order,
        ["lineage"] = Lineage,
        ["supersedes"] = Supersedes
    };

    public JsonObject ToJson() => new()
    {
        ["id"] = Id,
        ["lineage"] = Lineage,
        ["supersedes"] = Supersedes,
        ["type"] = Type,
        ["source"] = Source,
        ["order"] = Order,
        ["raw"] = Raw?.DeepClone()
    };

    public static RawItem? FromJson(JsonObject o)
    {
        if (o["type"]?.GetValue<string>() is not { } type) return null;
        var item = new RawItem(
            type,
            (o["raw"] as JsonObject) ?? new JsonObject(),
            o["source"]?.GetValue<string>() ?? "",
            o["order"]?.GetValue<int>() ?? 0,
            o["lineage"]?.GetValue<string>(),
            o["supersedes"]?.GetValue<string>());
        return item;
    }
}
