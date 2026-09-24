using System.Security.Cryptography;
using System.Text;

namespace Chrono.Core.Engine;

/// <summary>Deterministic stable identity. Equivalent section + anchor set + rule
/// version hashes to the same id, and ordering is normalized before hashing.</summary>
public static class CandidateId
{
    public static string Deterministic(string sectionId, System.Collections.Generic.IReadOnlyList<string> anchorIds, string ruleVersion)
    {
        var ordered = anchorIds.OrderBy(x => x, System.StringComparer.Ordinal).ToArray();
        var key = $"{sectionId}|{ruleVersion}|{string.Join(",", ordered)}";
        using var sha = SHA256.Create();
        var bytes = sha.ComputeHash(Encoding.UTF8.GetBytes(key));
        return "cand-" + Convert.ToHexString(bytes[..8]).ToLowerInvariant();
    }

    public static string Event(string type, string branch, long seq) => $"evt-{seq:D5}-{ShortHash($"{branch}:{type}:{seq}")}";

    public static string ShortHash(string s)
    {
        using var sha = SHA256.Create();
        return Convert.ToHexString(sha.ComputeHash(Encoding.UTF8.GetBytes(s))[..4]).ToLowerInvariant();
    }
}
