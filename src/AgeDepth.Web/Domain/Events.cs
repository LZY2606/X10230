using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace AgeDepth.Domain;

/// <summary>Append-only, hash-chained machine-readable record.
/// Every compute result records its input digest, parameter version and parent dependencies;
/// every accept/reject/parameter decision is an event and old events remain readable forever.</summary>
public sealed record EventRecord(
    long Seq,
    string Id,
    string Kind,
    string Payload,
    string InputDigest,
    string ParamVersion,
    string Parents,
    string PrevHash,
    string Hash,
    string CreatedAt)
{
    public T? PayloadAs<T>() => JsonSerializer.Deserialize<T>(Payload, JsonOptions);
    internal static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
    };
}

public enum ImportResult { Ok, CorruptFormat, HashChainBroken, WrongFormat }

public static class EventHasher
{
    public static string Hash(long seq, string id, string kind, string payload,
        string inputDigest, string paramVersion, string parents, string prevHash)
    {
        var body = string.Join("\n",
            seq.ToString(System.Globalization.CultureInfo.InvariantCulture),
            id, kind, Canonical(payload), inputDigest, paramVersion, Canonical(parents), prevHash);
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(body))).ToLowerInvariant();
    }

    /// <summary>Canonicalise a JSON payload so whitespace/key-order differences in an
    /// archive cannot change identity. Values survive unchanged.</summary>
    public static string Canonical(string json)
    {
        using var doc = JsonDocument.Parse(json);
        using var ms = new MemoryStream();
        using (var w = new Utf8JsonWriter(ms))
            Write(w, doc.RootElement);
        return Encoding.UTF8.GetString(ms.ToArray());
    }

    private static void Write(Utf8JsonWriter w, JsonElement e)
    {
        switch (e.ValueKind)
        {
            case JsonValueKind.Object:
                w.WriteStartObject();
                foreach (var prop in e.EnumerateObject().OrderBy(p => p.Name, StringComparer.Ordinal))
                {
                    w.WritePropertyName(prop.Name);
                    Write(w, prop.Value);
                }
                w.WriteEndObject();
                break;
            case JsonValueKind.Array:
                w.WriteStartArray();
                foreach (var item in e.EnumerateArray())
                    Write(w, item);
                w.WriteEndArray();
                break;
            case JsonValueKind.String: w.WriteStringValue(e.GetString()); break;
            case JsonValueKind.Number: w.WriteRawValue(e.GetRawText()); break;
            case JsonValueKind.True: w.WriteBooleanValue(true); break;
            case JsonValueKind.False: w.WriteBooleanValue(false); break;
            case JsonValueKind.Null: w.WriteNullValue(); break;
            default: w.WriteStringValue(e.GetRawText()); break;
        }
    }
}
