using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace ChronoCore.Engine;

internal static class Hashing
{
    private static readonly JsonSerializerOptions Opts = new() { DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull };

    public static string Sha256(object payload)
    {
        var json = JsonSerializer.Serialize(payload, Opts);
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(json)));
    }

    /// <summary>
    /// 等价输入得到稳定顺序：对同类材料按稳定键排序后再摘要，
    /// 使导入顺序不同但语义等价的输入产生相同摘要。
    /// </summary>
    public static string StableDigest<T>(IEnumerable<T> items, Func<T, string> stableKey, string ruleTag)
    {
        var ordered = items.Select(stableKey).Order(StringComparer.Ordinal).ToArray();
        return Sha256(new { ruleTag, ordered });
    }
}
