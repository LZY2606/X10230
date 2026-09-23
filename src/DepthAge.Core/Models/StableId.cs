using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace DepthAge.Core.Models;

/// <summary>
/// 稳定身份：相同规范化内容永远得到相同 id；导出/删除/再导入后保持不变。
/// </summary>
public static class StableId
{
    private static readonly JsonSerializerOptions CanonicalJson = new()
    {
        PropertyNamingPolicy = null,
        WriteIndented = false,
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull
    };

    public static string Compute(string prefix, object payload)
    {
        var json = JsonSerializer.Serialize(payload, CanonicalJson);
        return ComputeRaw(prefix, json);
    }

    public static string ComputeRaw(string prefix, string rawPayload)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(rawPayload));
        return $"{prefix}_{Convert.ToHexString(bytes, 0, 16).ToLowerInvariant()}";
    }
}
