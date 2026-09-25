using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace AgeDepth.Core;

/// <summary>
/// Stable, content-derived identities. Equivalent inputs must produce identical
/// ids and identical ordering regardless of import order or session.
/// </summary>
public static class Ids
{
    public static string ContentId(string prefix, JsonNode node)
        => $"{prefix}_{Digest(node)[..16]}";

    public static string Digest(JsonNode? node)
        => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(Canonicalize(node)))).ToLowerInvariant();

    public static string DigestText(string text)
        => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text))).ToLowerInvariant();

    /// <summary>Canonical JSON: object keys sorted ordinally, numbers invariant.</summary>
    public static string Canonicalize(JsonNode? node)
    {
        var sb = new StringBuilder();
        Write(node, sb);
        return sb.ToString();
    }

    private static void Write(JsonNode? n, StringBuilder sb)
    {
        switch (n)
        {
            case null:
                sb.Append("null");
                break;
            case JsonObject o:
                sb.Append('{');
                var first = true;
                foreach (var kv in o.OrderBy(k => k.Key, StringComparer.Ordinal))
                {
                    if (!first) sb.Append(',');
                    first = false;
                    sb.Append(JsonSerializer.Serialize(kv.Key));
                    sb.Append(':');
                    Write(kv.Value, sb);
                }
                sb.Append('}');
                break;
            case JsonArray a:
                sb.Append('[');
                for (var i = 0; i < a.Count; i++)
                {
                    if (i > 0) sb.Append(',');
                    Write(a[i], sb);
                }
                sb.Append(']');
                break;
            case JsonValue v:
                if (v.TryGetValue<string>(out var s)) sb.Append(JsonSerializer.Serialize(s));
                else if (v.TryGetValue<bool>(out var b)) sb.Append(b ? "true" : "false");
                else if (v.TryGetValue<double>(out var d)) sb.Append(d.ToString("G17", CultureInfo.InvariantCulture));
                else sb.Append(v.ToJsonString());
                break;
        }
    }
}
