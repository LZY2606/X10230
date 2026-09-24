using System.Globalization;
using System.Text.RegularExpressions;

namespace CoreChron.Domain;

// 导入时保留原始表示；这里只做“可转换性判定 + 规范化值”，失败留在原位置并附诊断
public static class QuantityParser
{
    private static readonly Regex Number = new(
        @"\s*([+-]?\d+(?:\.\d+)?(?:[eE][+-]?\d+)?)\s*([^\s;]*)?", RegexOptions.Compiled);

    public static DepthPoint ParseDepth(string raw, string? expectedDatum = null)
    {
        if (string.IsNullOrWhiteSpace(raw))
            return new DepthPoint(raw ?? "", null, expectedDatum, "DEPTH-EMPTY: 空深度值，保留原文不参与计算");

        var m = Number.Match(raw);
        if (!m.Success)
            return new DepthPoint(raw, null, expectedDatum, "DEPTH-PARSE: 无法解析数值与单位");

        double val = double.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture);
        string unit = m.Groups[2].Success ? m.Groups[2].Value.Trim() : "";

        string? diag = null;
        double? cm = null;
        switch (unit.ToLowerInvariant())
        {
            case "cm": cm = val; break;
            case "m": cm = val * 100.0; break;
            case "mm": cm = val / 10.0; break;
            case "":
                diag = $"DEPTH-UNIT-MISSING: 缺少单位，按 {expectedDatum ?? "?"} 下裸数保留，不做规范化";
                break;
            default:
                diag = $"DEPTH-UNIT-UNKNOWN: 未知深度单位 '{unit}'，值留在原位置";
                break;
        }

        if (cm.HasValue && expectedDatum is not null && raw.Contains('(') && !raw.Contains(expectedDatum, StringComparison.OrdinalIgnoreCase))
            diag ??= $"DEPTH-DATUM-MISMATCH: 原始基准与项目基准 {expectedDatum} 不一致";

        return new DepthPoint(raw, cm, expectedDatum, diag);
    }

    public static Quantity ParseOffsetYears(string raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
            return new Quantity(raw ?? "", null, null, null, "OFFSET-EMPTY: 空偏移值");

        var m = Number.Match(raw);
        if (!m.Success)
            return new Quantity(raw, null, null, null, "OFFSET-PARSE: 无法解析");

        double val = double.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture);
        string unit = m.Groups[2].Success ? m.Groups[2].Value.Trim() : "";
        return unit.ToLowerInvariant() switch
        {
            "a" or "yr" or "yrs" or "y" => new Quantity(raw, val, "a", "a BP", null),
            "ka" => new Quantity(raw, val * 1000.0, "a", "a BP", null),
            _ => new Quantity(raw, null, unit.Length == 0 ? null : unit, null,
                 unit.Length == 0 ? "OFFSET-UNIT-MISSING: 缺单位" : $"OFFSET-UNIT-UNKNOWN: '{unit}'")
        };
    }
}
