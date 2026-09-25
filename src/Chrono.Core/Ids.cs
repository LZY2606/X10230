using System.Globalization;
using System.Text;

namespace Chrono.Core;

/// <summary>
/// 稳定身份：来源显式 id 优先；缺省时由规范化内容的 FNV-1a 派生。
/// 导出→删除库→再导入后身份保持，等价输入得到等价（稳定）身份与顺序。
/// </summary>
public static class StableId
{
    public static ulong Fnv1a(string s)
    {
        const ulong offset = 14695981039346656037UL;
        const ulong prime = 1099511628211UL;
        var hash = offset;
        foreach (var b in Encoding.UTF8.GetBytes(s))
        {
            hash ^= b;
            hash *= prime;
        }
        return hash;
    }

    public static string Derive(string prefix, string canonicalContent)
        => $"{prefix}_{Fnv1a(canonicalContent):x16}";

    public static ulong SeedFor(string stableId, string purpose)
        => Fnv1a(purpose + "|" + stableId);

    public static string CanonicalKey(params (string k, string v)[] parts)
    {
        var sb = new StringBuilder();
        foreach (var (k, v) in parts.OrderBy(p => p.k, StringComparer.Ordinal))
            sb.Append(k).Append('=').Append(v).Append(';');
        return sb.ToString();
    }
}

public static class Fmt
{
    public static string Num(double v) => v.ToString("R", CultureInfo.InvariantCulture);
}
