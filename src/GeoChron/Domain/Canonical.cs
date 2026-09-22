using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace GeoChron.Domain;

/// <summary>
/// Canonical JSON: object keys sorted lexicographically, no insignificant whitespace.
/// Used for content-addressed identities, dependency summaries and export bundles.
/// </summary>
public static class CanonicalJson
{
    public static byte[] ToBytes(JsonNode? node)
    {
        using var ms = new MemoryStream();
        using (var w = new Utf8JsonWriter(ms, new JsonWriterOptions { Indented = false }))
            WriteNode(w, node);
        return ms.ToArray();
    }

    public static string ToText(JsonNode? node) => Encoding.UTF8.GetString(ToBytes(node));

    public static string HashHex(JsonNode? node, string prefix)
        => prefix + Convert.ToHexString(SHA1.HashData(ToBytes(node))).ToLowerInvariant();

    static void WriteNode(Utf8JsonWriter w, JsonNode? node)
    {
        switch (node)
        {
            case null:
                w.WriteNullValue();
                break;
            case JsonObject obj:
                w.WriteStartObject();
                foreach (var (key, val) in obj.OrderBy(kv => kv.Key, StringComparer.Ordinal))
                {
                    w.WritePropertyName(key);
                    WriteNode(w, val);
                }
                w.WriteEndObject();
                break;
            case JsonArray arr:
                w.WriteStartArray();
                foreach (var item in arr) WriteNode(w, item);
                w.WriteEndArray();
                break;
            case JsonValue v:
                WriteValue(w, v);
                break;
        }
    }

    static void WriteValue(Utf8JsonWriter w, JsonValue v)
    {
        var el = JsonSerializer.SerializeToElement(v);
        switch (el.ValueKind)
        {
            case JsonValueKind.String: w.WriteStringValue(el.GetString()); break;
            case JsonValueKind.Number:
                if (el.TryGetInt64(out var i)) w.WriteNumberValue(i);
                else if (el.TryGetDecimal(out var d)) w.WriteNumberValue(d);
                else w.WriteRawValue(el.GetRawText());
                break;
            case JsonValueKind.True: w.WriteBooleanValue(true); break;
            case JsonValueKind.False: w.WriteBooleanValue(false); break;
            default: w.WriteStringValue(el.GetRawText()); break;
        }
    }
}
