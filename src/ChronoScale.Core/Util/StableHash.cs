using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace ChronoScale.Core.Util;

/// <summary>
/// Canonical, key-sorted JSON serialization. Used for content hashes so that
/// equivalent inputs always produce identical digests regardless of property order.
/// </summary>
public static class CanonicalJson
{
    public static readonly JsonWriterOptions WriterOptions = new()
    {
        Indented = false,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };

    public static string Serialize(object? value)
    {
        using var ms = new MemoryStream();
        using (var w = new Utf8JsonWriter(ms, WriterOptions))
            WriteSorted(w, value);
        return Encoding.UTF8.GetString(ms.ToArray());
    }

    public static void WriteSorted(Utf8JsonWriter w, object? value)
    {
        switch (value)
        {
            case null:
                w.WriteNullValue();
                break;
            case IDictionary<string, object?> d:
                WriteDict(w, d);
                break;
            case System.Collections.IDictionary nonGen when nonGen.Count == 0:
                w.WriteStartObject();
                w.WriteEndObject();
                break;
            case System.Collections.IDictionary ng:
            {
                var sorted = new SortedDictionary<string, object?>();
                foreach (System.Collections.DictionaryEntry e in ng)
                    sorted[(string)e.Key] = e.Value;
                WriteDict(w, sorted);
                break;
            }
            case JsonElement je:
                WriteJsonElement(w, je);
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
            case double dbl when double.IsNaN(dbl) || double.IsInfinity(dbl):
                w.WriteStringValue(dbl.ToString("R"));
                break;
            case double d2:
                w.WriteNumberValue(decimal.Parse(d2.ToString("R"), System.Globalization.CultureInfo.InvariantCulture));
                break;
            case decimal dec:
                w.WriteNumberValue(dec);
                break;
            case System.Collections.IEnumerable en:
            {
                w.WriteStartArray();
                foreach (var item in en)
                    WriteSorted(w, item);
                w.WriteEndArray();
                break;
            }
            default:
            {
                var sorted = new SortedDictionary<string, object?>();
                foreach (var p in value.GetType().GetProperties())
                {
                    if (!p.CanRead) continue;
                    sorted[p.Name] = p.GetValue(value);
                }
                WriteDict(w, sorted);
                break;
            }
        }
    }

    private static void WriteDict(Utf8JsonWriter w, IDictionary<string, object?> d)
    {
        var sorted = new SortedDictionary<string, object?>(d);
        w.WriteStartObject();
        foreach (var (k, v) in sorted)
        {
            w.WritePropertyName(k);
            WriteSorted(w, v);
        }
        w.WriteEndObject();
    }

    private static void WriteJsonElement(Utf8JsonWriter w, JsonElement je)
    {
        switch (je.ValueKind)
        {
            case JsonValueKind.Object:
            {
                var sorted = new SortedDictionary<string, JsonElement>();
                foreach (var p in je.EnumerateObject())
                    sorted[p.Name] = p.Value;
                w.WriteStartObject();
                foreach (var (k, v) in sorted)
                {
                    w.WritePropertyName(k);
                    WriteJsonElement(w, v);
                }
                w.WriteEndObject();
                break;
            }
            case JsonValueKind.Array:
                w.WriteStartArray();
                foreach (var item in je.EnumerateArray())
                    WriteJsonElement(w, item);
                w.WriteEndArray();
                break;
            case JsonValueKind.String:
                w.WriteStringValue(je.GetString());
                break;
            case JsonValueKind.Number:
                w.WriteRawValue(je.GetRawText());
                break;
            case JsonValueKind.True:
                w.WriteBooleanValue(true);
                break;
            case JsonValueKind.False:
                w.WriteBooleanValue(false);
                break;
            case JsonValueKind.Null:
                w.WriteNullValue();
                break;
            default:
                w.WriteStringValue(je.GetRawText());
                break;
        }
    }

    /// <summary>
    /// Parse arbitrary JSON into nested dictionaries/lists so that two files with
    /// different property ordering hash identically.
    /// </summary>
    public static object? ParseToCanonical(string json)
    {
        using var doc = JsonDocument.Parse(json);
        return FromElement(doc.RootElement);
    }

    public static object? FromElement(JsonElement je)
    {
        switch (je.ValueKind)
        {
            case JsonValueKind.Object:
            {
                var d = new Dictionary<string, object?>();
                foreach (var p in je.EnumerateObject())
                    d[p.Name] = FromElement(p.Value);
                return d;
            }
            case JsonValueKind.Array:
                return je.EnumerateArray().Select(FromElement).ToList();
            case JsonValueKind.String: return je.GetString();
            case JsonValueKind.Number: return je.TryGetInt64(out var l) ? l : je.GetDouble();
            case JsonValueKind.True: return true;
            case JsonValueKind.False: return false;
            default: return null;
        }
    }
}

public static class ContentHash
{
    public static string Sha256(object? canonicalizable)
    {
        var json = CanonicalJson.Serialize(canonicalizable);
        return Sha256Hex(json);
    }

    public static string Sha256Hex(string text)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(text));
        return Convert.ToHexString(bytes).ToLowerInvariant();
    }

    /// <summary>Stable 12-hex-char prefix used for human-readable deterministic ids.</summary>
    public static string Short(object? canonicalizable)
    {
        var full = Sha256(canonicalizable);
        return full[..12];
    }
}

/// <summary>Deterministic, seedable PRNG (SplitMix64) so Monte Carlo runs replay exactly.</summary>
public sealed class DeterministicRng
{
    private ulong _state;

    public DeterministicRng(ulong seed) => _state = seed;

    public ulong NextUInt64()
    {
        _state += 0x9E3779B97F4A7C15UL;
        var z = _state;
        z = (z ^ (z >> 30)) * 0xBF58476D1CE4E5B9UL;
        z = (z ^ (z >> 27)) * 0x94D049BB133111EBUL;
        return z ^ (z >> 31);
    }

    public double NextDouble() => (NextUInt64() >> 11) * (1.0 / 9007199254740992.0);

    /// <summary>Box-Muller standard normal.</summary>
    public double NextGaussian()
    {
        double u1, u2;
        do { u1 = NextDouble(); } while (u1 <= 0.0);
        u2 = NextDouble();
        return Math.Sqrt(-2.0 * Math.Log(u1)) * Math.Cos(2.0 * Math.PI * u2);
    }

    public static ulong SeedFromString(string s)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(s));
        return BitConverter.ToUInt64(bytes, 0);
    }
}
