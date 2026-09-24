using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Chrono.Domain;

/// <summary>规范化 JSON 与稳定摘要：等价输入 → 稳定顺序/身份。</summary>
public static class Canonical
{
    public static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
    };

    public static string Serialize(object? value) =>
        JsonSerializer.Serialize(value, Json);

    public static string Digest(object? value)
    {
        var bytes = Encoding.UTF8.GetBytes(Serialize(value));
        return Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
    }

    public static string DigestText(string text) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text))).ToLowerInvariant();

    public static ulong SeedFrom(string text)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(text));
        return BitConverter.ToUInt64(bytes, 0);
    }

    public static string ShortId(string prefix, string material) =>
        $"{prefix}:{DigestText(material)[..12]}";
}
