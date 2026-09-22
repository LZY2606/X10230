using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace ChronoCore.Domain;

/// <summary>Deterministic, stable identity for derivations: same inputs + rule version => same id.</summary>
public static class StableId
{
    public static string Of(params object?[] parts)
    {
        var sb = new StringBuilder();
        foreach (var p in parts)
        {
            if (p is IEnumerable<object?> many)
            {
                foreach (var item in many) sb.Append(item).Append('|');
            }
            else
            {
                sb.Append(p).Append('|');
            }
        }
        return "sha256:" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(sb.ToString())))
            .ToLowerInvariant()[..16];
    }

    public static string Digest(object payload)
    {
        var json = JsonSerializer.Serialize(payload, JsonOptions.Compact);
        return "sha256:" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(json))).ToLowerInvariant();
    }
}

public static class JsonOptions
{
    public static readonly JsonSerializerOptions Compact = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = false
    };

    public static readonly JsonSerializerOptions Pretty = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true
    };
}
