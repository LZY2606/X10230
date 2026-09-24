using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Chrono.Engine;

/// <summary>
/// 规范化序列化：键排序、无多余空白。跨进程稳定，用于输入摘要/等优排序/身份派生。
/// </summary>
public static class Canonical
{
    public static string Of(JsonElement el)
    {
        using var ms = new MemoryStream();
        using (var w = new Utf8JsonWriter(ms, new JsonWriterOptions { Indented = false }))
            WriteElement(w, el);
        return Encoding.UTF8.GetString(ms.ToArray());
    }

    public static string OfString(string text)
    {
        using var doc = JsonDocument.Parse(text);
        return Of(doc.RootElement);
    }

    public static string Sha256(string text)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(text));
        return Convert.ToHexString(bytes).ToLowerInvariant();
    }

    public static string ShortHash(string text) => Sha256(text)[..12];

    private static void WriteElement(Utf8JsonWriter w, JsonElement el)
    {
        switch (el.ValueKind)
        {
            case JsonValueKind.Object:
            {
                w.WriteStartObject();
                foreach (var prop in el.EnumerateObject().OrderBy(p => p.Name, StringComparer.Ordinal))
                {
                    w.WritePropertyName(prop.Name);
                    WriteElement(w, prop.Value);
                }
                w.WriteEndObject();
                break;
            }
            case JsonValueKind.Array:
            {
                w.WriteStartArray();
                foreach (var item in el.EnumerateArray()) WriteElement(w, item);
                w.WriteEndArray();
                break;
            }
            case JsonValueKind.String: w.WriteStringValue(el.GetString()); break;
            case JsonValueKind.Number: w.WriteRawValue(el.GetRawText()); break;
            case JsonValueKind.True: w.WriteBooleanValue(true); break;
            case JsonValueKind.False: w.WriteBooleanValue(false); break;
            case JsonValueKind.Null: w.WriteNullValue(); break;
            default: w.WriteStringValue(el.GetRawText()); break;
        }
    }
}
