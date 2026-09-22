namespace ChronoCore.Domain;

/// <summary>稳定身份：由导入材料显式提供，跨导出/导入保持不变。</summary>
public static class IdKinds
{
    public const string Project = "prj";
    public const string Core = "cor";
    public const string Segment = "seg";
    public const string Boundary = "bnd";
    public const string Anchor = "anc";
    public const string Reservoir = "res";
    public const string EventLayer = "evt";
    public const string Material = "mat";
    public const string Exclusion = "exc";
    public const string Event = "ev";
    public const string RuleSet = "rul";
    public const string Candidate = "mdl";
    public const string ModelNode = "mno";
    public const string Freeze = "frz";
    public const string Decision = "dec";
    public const string Value = "val";
    public const string Projected = "prjval";
    public const string Scenario = "scn";
    public const string ScenarioStep = "step";
}

public static class Hashing
{
    public static string Sha256Hex(ReadOnlySpan<byte> bytes)
    {
        var hash = System.Security.Cryptography.SHA256.HashData(bytes);
        return Convert.ToHexString(hash).ToLowerInvariant();
    }

    public static string Sha256Hex(string text) => Sha256Hex(System.Text.Encoding.UTF8.GetBytes(text));

    /// <summary>确定性 JSON 序列化（按属性名排序），用于输入摘要与链式指纹。</summary>
    public static string CanonicalJson(object? value)
    {
        var opts = new System.Text.Json.JsonSerializerOptions
        {
            PropertyNamingPolicy = null,
            WriteIndented = false,
            DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull
        };
        using var ms = new MemoryStream();
        using (var w = new System.Text.Json.Utf8JsonWriter(ms))
            WriteCanonical(w, value);
        return System.Text.Encoding.UTF8.GetString(ms.ToArray());
    }

    private static void WriteCanonical(System.Text.Json.Utf8JsonWriter w, object? value)
    {
        switch (value)
        {
            case null:
                w.WriteNullValue();
                break;
            case string s:
                w.WriteStringValue(s);
                break;
            case bool b:
                w.WriteBooleanValue(b);
                break;
            case int i:
                w.WriteNumberValue(i);
                break;
            case long l:
                w.WriteNumberValue(l);
                break;
            case double d:
                w.WriteNumberValue(d);
                break;
            case decimal m:
                w.WriteNumberValue(m);
                break;
            case IDictionary<string, object?> dict:
                WriteMap(w, dict);
                break;
            case System.Text.Json.JsonElement je:
                WriteJsonElement(w, je);
                break;
            default:
                var t = value.GetType();
                if (t.IsEnum) { w.WriteStringValue(value.ToString()); break; }
                if (value is System.Collections.IEnumerable enumerable and not string)
                {
                    w.WriteStartArray();
                    foreach (var item in enumerable)
                        WriteCanonical(w, item);
                    w.WriteEndArray();
                    break;
                }
                var props = t.GetProperties()
                    .Where(p => p.CanRead)
                    .Select(p => (p.Name, Val: p.GetValue(value)))
                    .Where(kv => kv.Val is not null)
                    .OrderBy(kv => kv.Name, StringComparer.Ordinal)
                    .ToList();
                w.WriteStartObject();
                foreach (var (name, val) in props)
                {
                    w.WritePropertyName(name);
                    WriteCanonical(w, val);
                }
                w.WriteEndObject();
                break;
        }
    }

    private static void WriteMap(System.Text.Json.Utf8JsonWriter w, IDictionary<string, object?> dict)
    {
        w.WriteStartObject();
        foreach (var kv in dict.OrderBy(k => k.Key, StringComparer.Ordinal))
        {
            w.WritePropertyName(kv.Key);
            WriteCanonical(w, kv.Value);
        }
        w.WriteEndObject();
    }

    private static void WriteJsonElement(System.Text.Json.Utf8JsonWriter w, System.Text.Json.JsonElement el)
    {
        switch (el.ValueKind)
        {
            case System.Text.Json.JsonValueKind.Object:
                w.WriteStartObject();
                foreach (var prop in el.EnumerateObject().OrderBy(p => p.Name, StringComparer.Ordinal))
                {
                    w.WritePropertyName(prop.Name);
                    WriteJsonElement(w, prop.Value);
                }
                w.WriteEndObject();
                break;
            case System.Text.Json.JsonValueKind.Array:
                w.WriteStartArray();
                foreach (var item in el.EnumerateArray())
                    WriteJsonElement(w, item);
                w.WriteEndArray();
                break;
            case System.Text.Json.JsonValueKind.String:
                w.WriteStringValue(el.GetString());
                break;
            case System.Text.Json.JsonValueKind.Number:
                w.WriteRawValue(el.GetRawText());
                break;
            case System.Text.Json.JsonValueKind.True:
                w.WriteBooleanValue(true);
                break;
            case System.Text.Json.JsonValueKind.False:
                w.WriteBooleanValue(false);
                break;
            default:
                w.WriteNullValue();
                break;
        }
    }
}
