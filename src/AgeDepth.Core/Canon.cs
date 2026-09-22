using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace AgeDepth.Core;

/// <summary>
/// Canonical JSON: object keys sorted ordinally, numbers emitted round-trippable.
/// Order-insensitive inputs therefore produce identical canonical text and digest.
/// </summary>
public static class Canon
{
    public static string Serialize(JsonNode? node)
    {
        var sb = new StringBuilder();
        WriteNode(node, sb);
        return sb.ToString();
    }

    public static string Digest(string canonicalJson)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(canonicalJson));
        return Convert.ToHexStringLower(bytes);
    }

    private static void WriteNode(JsonNode? node, StringBuilder sb)
    {
        if (node is null) { sb.Append("null"); return; }
        switch (node)
        {
            case JsonObject obj:
                sb.Append('{');
                var first = true;
                foreach (var kv in obj.OrderBy(p => p.Key, StringComparer.Ordinal))
                {
                    if (!first) sb.Append(',');
                    first = false;
                    WriteString(kv.Key, sb);
                    sb.Append(':');
                    WriteNode(kv.Value, sb);
                }
                sb.Append('}');
                break;
            case JsonArray arr:
                sb.Append('[');
                for (var i = 0; i < arr.Count; i++)
                {
                    if (i > 0) sb.Append(',');
                    WriteNode(arr[i], sb);
                }
                sb.Append(']');
                break;
            default:
                WriteScalar(node, sb);
                break;
        }
    }

    private static void WriteScalar(JsonNode node, StringBuilder sb)
    {
        var element = JsonSerializer.SerializeToElement(node);
        switch (element.ValueKind)
        {
            case JsonValueKind.True: sb.Append("true"); break;
            case JsonValueKind.False: sb.Append("false"); break;
            case JsonValueKind.String: WriteString(element.GetString()!, sb); break;
            case JsonValueKind.Number: sb.Append(element.GetRawText()); break;
            default: sb.Append("null"); break;
        }
    }

    private static void WriteString(string value, StringBuilder sb)
    {
        sb.Append('"');
        foreach (var c in value)
        {
            switch (c)
            {
                case '"': sb.Append("\\\""); break;
                case '\\': sb.Append("\\\\"); break;
                case '\b': sb.Append("\\b"); break;
                case '\f': sb.Append("\\f"); break;
                case '\n': sb.Append("\\n"); break;
                case '\r': sb.Append("\\r"); break;
                case '\t': sb.Append("\\t"); break;
                default:
                    if (c < 0x20) sb.Append("\\u").Append(((int)c).ToString("x4"));
                    else sb.Append(c);
                    break;
            }
        }
        sb.Append('"');
    }

    /// <summary>Deterministic seed derived from a digest string.</summary>
    public static ulong SeedFrom(string digest, ulong salt = 0)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(digest + ":" + salt));
        return BitConverter.ToUInt64(bytes, 0) | 1UL;
    }
}

/// <summary>Deterministic xorshift64* PRNG; same seed always yields the same stream.</summary>
public sealed class Xorshift
{
    private ulong _state;
    public Xorshift(ulong state) => _state = state == 0 ? 1 : state;
    public ulong Next()
    {
        var x = _state;
        x ^= x >> 12; x ^= x << 25; x ^= x >> 27;
        _state = x;
        return unchecked(x * 2685821657736338717UL);
    }
    public int Index(int count) => (int)(Next() % (ulong)count);
}
