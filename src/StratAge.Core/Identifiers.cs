using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace StratAge.Core;

/// <summary>
/// 稳定身份：同一项目命名空间内 id 由内容决定（导入材料）或由父+序号决定（派生事件）。
/// 因此导出→删除→再导入后身份、顺序、来源关系一致；等价输入得到稳定顺序。
/// </summary>
public static class StableIds
{
    public static string Sha256Hex(string s) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(s))).ToLowerInvariant();

    public static string For(string ns, string content)
    {
        var h = Sha256Hex($"{ns}\u0000{content}");
        return $"{ns}-{h[..16]}";
    }

    public static string Event(string projectId, int sequence) =>
        $"evt-{projectId}-{sequence:D5}";

    public static string Model(string projectId, string branch, string paramVersion, int rank, string digest)
        => For("mdl", $"{projectId}|{branch}|{paramVersion}|{rank}|{digest}");
}

public static class Canonical
{
    public static readonly JsonSerializerOptions JsonOpts = new()
    {
        WriteIndented = false,
        DefaultIgnoreCondition = JsonIgnoreCondition.Never,
    };

    public static string DigestOf(object o)
    {
        var json = JsonSerializer.Serialize(o, JsonOpts);
        return StableIds.Sha256Hex(json);
    }
}
