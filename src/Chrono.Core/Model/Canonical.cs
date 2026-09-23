using System.Text;
using System.Text.Json;

namespace Chrono.Core.Model;

/// <summary>
/// Deterministic canonical JSON (object keys sorted ordinal) and stable content identities.
/// Identities survive export/delete/re-import because they derive only from content.
/// </summary>
public static class Canonical
{
    public static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = null,
        WriteIndented = false,
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.Never
    };

    public static string Of(object? value)
    {
        var bytes = JsonSerializer.SerializeToUtf8Bytes(value, JsonOptions);
        using var doc = JsonDocument.Parse(bytes);
        return Render(doc.RootElement);
    }

    public static string Render(JsonElement element)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
            {
                var sb = new StringBuilder();
                sb.Append('{');
                var first = true;
                foreach (var prop in element.EnumerateObject()
                             .OrderBy(p => p.Name, StringComparer.Ordinal))
                {
                    if (!first) sb.Append(',');
                    first = false;
                    sb.Append(Quote(prop.Name)).Append(':').Append(Render(prop.Value));
                }
                sb.Append('}');
                return sb.ToString();
            }
            case JsonValueKind.Array:
                return "[" + string.Join(",", element.EnumerateArray().Select(Render)) + "]";
            case JsonValueKind.String:
                return Quote(element.GetString() ?? string.Empty);
            case JsonValueKind.Number:
                return element.GetRawText();
            case JsonValueKind.True:
                return "true";
            case JsonValueKind.False:
                return "false";
            default:
                return "null";
        }
    }

    public static string Quote(string s)
    {
        var sb = new StringBuilder(s.Length + 2);
        sb.Append('"');
        foreach (var c in s)
        {
            switch (c)
            {
                case '"': sb.Append("\\\""); break;
                case '\\': sb.Append("\\\\"); break;
                case '\n': sb.Append("\\n"); break;
                case '\r': sb.Append("\\r"); break;
                case '\t': sb.Append("\\t"); break;
                default:
                    if (c < 0x20) sb.Append($"\\u{(int)c:x4}");
                    else sb.Append(c);
                    break;
            }
        }
        sb.Append('"');
        return sb.ToString();
    }

    /// <summary>FNV-1a 64-bit, rendered as 12 lowercase hex chars.</summary>
    public static string Fnv1a12(string text)
    {
        ulong hash = 1469598103934665603UL;
        foreach (var b in Encoding.UTF8.GetBytes(text))
        {
            hash ^= b;
            hash *= 1099511628211UL;
        }
        return hash.ToString("x16")[..12];
    }

    public static string StableId(string prefix, object? content)
        => $"{prefix}_{Fnv1a12(Of(content))}";
}
