using System.Globalization;
using System.Text.RegularExpressions;
using AgeDepthStudio.Domain;

namespace AgeDepthStudio.Engine;

/// <summary>
/// 深度解析：导入时保留原始表示；只在派生层规范化到米。
/// 无法转换时返回 null，并由调用方附带位置的诊断（原值留在原位，不删除）。
/// </summary>
public static partial class Depth
{
    [GeneratedRegex(@"^\s*(?<n>-?\d+(?:\.\d+)?)\s*(?<u>cm|mm|m)?\b", RegexOptions.IgnoreCase)]
    private static partial Regex Num();

    public static bool TryParseMeters(string? raw, out double meters)
    {
        meters = 0;
        if (string.IsNullOrWhiteSpace(raw) || raw.Trim() is "?" or "-" or "NA" or "N/A") return false;
        var m = Num().Match(raw);
        if (!m.Success) return false;
        if (!double.TryParse(m.Groups["n"].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out var v)) return false;
        var u = m.Groups["u"].Success ? m.Groups["u"].Value.ToLowerInvariant() : "m";
        meters = u switch { "cm" => v / 100.0, "mm" => v / 1000.0, _ => v };
        return true;
    }
}
