using System;
using System.Globalization;
using System.Linq;
using System.Security.Cryptography;
using System.Text;

namespace GSB.Core;

/// <summary>
/// 稳定身份：身份只由“对象种类 + 来源键 + 内容指纹”决定，
/// 因此等价输入在任何机器、任何一次导入中都得到相同 ID（稳定顺序的基础）。
/// 时间、随机数、自增主键都不参与。
/// </summary>
public static class StableId
{
    public static string Of(string kind, params (string Key, string Value)[] parts)
        => Of(kind, (IEnumerable<(string, string)>)parts);

    public static string Of(string kind, IEnumerable<(string Key, string Value)> parts)
    {
        var sb = new StringBuilder();
        sb.Append(kind).Append('|');
        foreach (var (k, v) in parts.OrderBy(p => p.Key, StringComparer.Ordinal))
            sb.Append(k).Append('=').Append(v ?? "").Append(';');
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(sb.ToString()));
        return kind + "-" + Convert.ToHexString(bytes)[..10].ToLowerInvariant();
    }

    public static string Numeric(double d) =>
        d.ToString("0.##########", CultureInfo.InvariantCulture);
}
