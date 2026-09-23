using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace DepthAge.Domain;

/// <summary>
/// Canonical JSON + content hashing. Every digest, derivation id and content-addressed
/// identity in the system flows through here so that equivalent inputs always produce
/// identical, stable identities regardless of process or platform.
/// </summary>
public static class Canon
{
    public static readonly JsonSerializerOptions Json = Create();

    private static JsonSerializerOptions Create()
    {
        var options = new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
            WriteIndented = false,
            NumberHandling = JsonNumberHandling.AllowNamedFloatingPointLiterals
        };
        options.Converters.Add(new JsonStringEnumConverter(JsonNamingPolicy.CamelCase));
        return options;
    }

    public static string Serialize<T>(T value) => JsonSerializer.Serialize(value, Json);

    public static T? Deserialize<T>(string json) => JsonSerializer.Deserialize<T>(json, Canon.Json);

    public static string Sha256Hex(string text)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(text));
        return Convert.ToHexString(bytes).ToLowerInvariant();
    }

    /// <summary>Digest of a canonically serialized value.</summary>
    public static string DigestOf<T>(T value) => Sha256Hex(Serialize(value));

    /// <summary>Stable identity derived from content: "prefix-" + first 16 hex chars of the digest.</summary>
    public static string StableId<T>(string prefix, T content) => prefix + "-" + DigestOf(content)[..16];
}
