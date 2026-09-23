using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace CoreChronology.Core;

public static class ChronoHashes
{
    private static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web);

    public static string Stable(object? value)
    {
        var json = JsonSerializer.Serialize(value, Options);
        return StableString(json);
    }

    public static string StableString(string value) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value)))[..16].ToLowerInvariant();

    public static string Digest(params object?[] values) => Stable(values);

    public static string Join(params string?[] values) => string.Join("|", values);
}
