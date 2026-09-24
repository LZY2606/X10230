using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace CoreChron.Domain;

public static class Digest
{
    private static readonly JsonSerializerOptions Opt = new()
    {
        PropertyNamingPolicy = null
    };

    public static string Of(object value)
    {
        var json = JsonSerializer.Serialize(value, value?.GetType() ?? typeof(object), Opt);
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(json)))[..16];
    }

    public static string Combine(params string[] parts)
        => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(string.Join("|", parts))))[..16];
}
