using System.Globalization;

namespace StratAge.Core;

/// <summary>
/// 领域量：原始表示永远保留（RawText/RawUnit），仅派生层做规范化。
/// 无法转换时 Normalized 为 null，并带诊断；绝不静默丢值或造默认值。
/// </summary>
public sealed record Qty(
    double? Value,
    string RawUnit,
    string? RawText,
    double? Normalized,
    string CanonicalUnit,
    IReadOnlyList<Diag> Diagnostics)
{
    public bool Converts => Normalized is not null;

    public string Display()
    {
        if (Normalized is double n)
            return string.Create(CultureInfo.InvariantCulture, $"{n:0.######} {CanonicalUnit}");
        return RawText is { Length: > 0 } ? RawText : $"{Value?.ToString(CultureInfo.InvariantCulture) ?? "?"} {RawUnit}";
    }
}

public static class UnitSystem
{
    // 深度 -> cm；年龄 -> yr BP（以 datumCalendar 声明的基准）；速率 -> cm/yr
    private static readonly Dictionary<string, (double factor, string canon)> Depth = new()
    {
        ["cm"] = (1.0, "cm"), ["m"] = (100.0, "cm"), ["mm"] = (0.1, "cm"),
    };
    private static readonly Dictionary<string, (double factor, string canon)> Age = new()
    {
        ["yr bp"] = (1.0, "yr BP"), ["yr"] = (1.0, "yr BP"),
        ["ka bp"] = (1000.0, "yr BP"), ["ka"] = (1000.0, "yr BP"),
        ["yr b2k"] = (1.0, "yr b2k"),
    };

    public static Qty ParseDepth(object? raw, string pointer, string? objectId = null)
        => Parse(raw, Depth, "cm", pointer, objectId);

    public static Qty ParseAge(object? raw, string pointer, string? objectId = null)
        => Parse(raw, Age, "yr BP", pointer, objectId);

    private static Qty Parse(object? raw, Dictionary<string, (double f, string c)> table,
        string canon, string pointer, string? objectId)
    {
        var diags = new List<Diag>();
        switch (raw)
        {
            case null:
                diags.Add(new Diag(Diag.IncompleteInput, "error",
                    "缺少数值：输入不完整，不允许用空值或默认值伪装成功", pointer, objectId));
                return new Qty(null, "", null, null, canon, diags);
            case string s:
            {
                var text = s.Trim();
                var m = System.Text.RegularExpressions.Regex.Match(
                    text, @"^([-+]?\d*\.?\d+(?:[eE][-+]?\d+)?)\s*([A-Za-z%/ ]*)$");
                if (!m.Success)
                {
                    // 无法转换的值留在原位置
                    diags.Add(new Diag(Diag.Unconvertible, "warning",
                        $"值无法转换为 {canon}，保留原始表示：'{text}'", pointer, objectId));
                    return new Qty(null, "", text, null, canon, diags);
                }
                var v = double.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture);
                var u = m.Groups[2].Value.Trim().ToLowerInvariant();
                return Build(v, u, text, table, canon, pointer, objectId, diags);
            }
            case double d or float or int or long:
            {
                var v = Convert.ToDouble(raw, CultureInfo.InvariantCulture);
                return Build(v, canon.ToLowerInvariant(), v.ToString(CultureInfo.InvariantCulture),
                    table, canon, pointer, objectId, diags);
            }
            default:
                diags.Add(new Diag(Diag.FormatCorrupt, "error",
                    $"数值字段类型无法识别：{raw.GetType().Name}", pointer, objectId));
                return new Qty(null, "", raw.ToString(), null, canon, diags);
        }
    }

    private static Qty Build(double v, string u, string rawText,
        Dictionary<string, (double f, string c)> table, string canon,
        string pointer, string? objectId, List<Diag> diags)
    {
        if (!table.TryGetValue(u, out var entry))
        {
            diags.Add(new Diag(Diag.UnitUnknown, "warning",
                $"未知单位 '{u}'，值保留在原位置不参与规范化", pointer, objectId));
            return new Qty(v, u, rawText, null, canon, diags);
        }
        return new Qty(v, u, rawText, v * entry.f, entry.c, diags);
    }

    public static double? RateCmPerYr(double depthCm, double ageYr) =>
        ageYr == 0 ? null : depthCm / ageYr;
}
