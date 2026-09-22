using System.Security.Cryptography;
using System.Text;

namespace Chrono.Core.Model;

/// <summary>
/// Deterministic, content-derived identifiers. Equivalent inputs always produce the same id and the
/// same ordering; ids survive export/import and never depend on insertion rowids.
/// </summary>
public static class StableId
{
    public static string Sha1(string kind, string canonicalPayload)
    {
        var bytes = SHA1.HashData(Encoding.UTF8.GetBytes(kind + "\n" + canonicalPayload));
        return kind + "_" + Convert.ToHexString(bytes)[..16].ToLowerInvariant();
    }

    public static string Of(object? value)
    {
        if (value is null) return "null";
        if (value is bool b) return b ? "true" : "false";
        if (value is System.Globalization.CultureInfo) return "";
        if (value is System.Collections.IDictionary dict)
        {
            var keys = dict.Keys.Cast<object>().Select(k => k.ToString() ?? "").OrderBy(k => k, StringComparer.Ordinal);
            return "{" + string.Join(",", keys.Select(k => Escape(k) + ":" + Of(dict[k]))) + "}";
        }
        if (value is System.Collections.IEnumerable en && value is not string)
        {
            return "[" + string.Join(",", en.Cast<object?>().Select(Of)) + "]";
        }
        return Escape(value.ToString() ?? "");
    }

    private static string Escape(string s) => "\"" + s.Replace("\\", "\\\\").Replace("\"", "\\\"") + "\"";
}
