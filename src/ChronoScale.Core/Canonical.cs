using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using System.Text.Json;

namespace ChronoScale.Core;

/// <summary>Deterministic canonical JSON (object keys sorted) used for input digests and stable derived ids.</summary>
public static class Canonical
{
    public static string Canonicalize(JsonNode? node)
    {
        using var ms = new MemoryStream();
        using (var w = new Utf8JsonWriter(ms))
            Write(node, w);
        return Encoding.UTF8.GetString(ms.ToArray());
    }

    public static string Hash(string canonical)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(canonical));
        return Convert.ToHexString(bytes).ToLowerInvariant();
    }

    public static string ShortHash(string canonical, int len = 12)
        => Hash(canonical)[..len];

    private static void Write(JsonNode? node, Utf8JsonWriter w)
    {
        switch (node)
        {
            case null: w.WriteNullValue(); break;
            case JsonObject o:
                w.WriteStartObject();
                foreach (var kv in o.OrderBy(k => k.Key, StringComparer.Ordinal))
                {
                    w.WritePropertyName(kv.Key);
                    Write(kv.Value, w);
                }
                w.WriteEndObject();
                break;
            case JsonArray a:
                w.WriteStartArray();
                foreach (var item in a) Write(item, w);
                w.WriteEndArray();
                break;
            case JsonValue v:
                WriteValue(v, w);
                break;
        }
    }

    private static void WriteValue(JsonValue v, Utf8JsonWriter w)
    {
        if (v.TryGetValue(out string? s)) { w.WriteStringValue(s); return; }
        if (v.TryGetValue(out int i)) { w.WriteNumberValue(i); return; }
        if (v.TryGetValue(out long l)) { w.WriteNumberValue(l); return; }
        if (v.TryGetValue(out double d)) { w.WriteNumberValue(d); return; }
        if (v.TryGetValue(out decimal dec)) { w.WriteNumberValue(dec); return; }
        if (v.TryGetValue(out bool b)) { w.WriteBooleanValue(b); return; }
        if (v.TryGetValue(out DateTimeOffset dto)) { w.WriteStringValue(dto); return; }
        w.WriteRawValue(v.ToJsonString());
    }
}
