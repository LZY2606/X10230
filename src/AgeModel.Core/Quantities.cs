using System.Globalization;
using System.Text.Json.Serialization;

namespace AgeModel.Core;

/// <summary>深度坐标基准（mbsf = 海床以下；mcd = 综合深度；mblf = 地表以下）。</summary>
public enum DepthDatum { Mbsf, Mcd, Mblf }

/// <summary>年龄时间基准（ka BP = 1950 年前千年；cal ka BP = 校正后）。</summary>
public enum AgeScale { KaBp, CalKaBp }

/// <summary>
/// 带单位与基准的领域量。<see cref="Raw"/> 保留导入时的原始表示，
/// 仅当能确定性转换到规范单位时才填写 <see cref="Value"/>。
/// </summary>
public sealed record Quantity(
    [property: JsonPropertyName("raw")] string Raw,
    [property: JsonPropertyName("value")] double? Value,
    [property: JsonPropertyName("unit")] string Unit,
    [property: JsonPropertyName("datum")] string? Datum)
{
    public bool IsConvertible => Value.HasValue && !double.IsNaN(Value.Value);

    public static Quantity Depth(double meters, DepthDatum datum = DepthDatum.Mbsf)
        => new(meters.ToString("G", CultureInfo.InvariantCulture) + " m", meters, "m", datum.ToString().ToLowerInvariant());

    public static Quantity RawOnly(string raw, string unit, string? datum = null)
        => new(raw, null, unit, datum);
}

/// <summary>单位换算结果：无法换算时 <see cref="Converted"/> 为 null，并带定位诊断。</summary>
public sealed record ConversionResult(double? Converted, string? Error)
{
    public bool Ok => Converted.HasValue;
}

/// <summary>深度单位与基准换算。无法换算的值留在原位置（不丢数据、不伪造成功）。</summary>
public static class DepthConverter
{
    private static readonly Dictionary<string, double> LengthToMeters = new(StringComparer.OrdinalIgnoreCase)
    {
        ["m"] = 1.0, ["meter"] = 1.0, ["meters"] = 1.0, ["metre"] = 1.0, ["metres"] = 1.0,
        ["cm"] = 0.01, ["centimeter"] = 0.01, ["centimeters"] = 0.01,
        ["mm"] = 0.001, ["ft"] = 0.3048, ["feet"] = 0.3048,
    };

    public static ConversionResult Parse(string raw, DepthDatum targetDatum = DepthDatum.Mbsf)
    {
        if (string.IsNullOrWhiteSpace(raw))
            return new ConversionResult(null, "深度为空");
        var s = raw.Trim();
        var datumNote = string.Empty;
        var lower = s.ToLowerInvariant();
        if (lower.Contains("mcd")) { datumNote = "mcd→mbsf 需要井深校正（splice offset），本案例无对应参数"; }
        var unitStart = -1;
        foreach (var u in LengthToMeters.Keys.OrderByDescending(k => k.Length))
        {
            var idx = lower.IndexOf(u, StringComparison.OrdinalIgnoreCase);
            if (idx >= 0 && idx > 0) { unitStart = idx; break; }
        }
        string numPart;
        string unit;
        if (unitStart >= 0)
        {
            numPart = s[..unitStart].Trim();
            unit = s[unitStart..].Trim();
        }
        else
        {
            numPart = s.Replace("mbsf", "").Replace("mcd", "").Replace("mblf", "").Trim();
            unit = "m";
        }
        if (!double.TryParse(numPart, NumberStyles.Float, CultureInfo.InvariantCulture, out var v))
            return new ConversionResult(null, $"无法解析深度数值: '{raw}'");
        if (!LengthToMeters.TryGetValue(unit.ToLowerInvariant(), out var factor))
            return new ConversionResult(null, $"未知/不支持的长度单位: '{unit}'");
        if (!string.IsNullOrEmpty(datumNote))
            return new ConversionResult(null, datumNote);
        if (v < 0)
            return new ConversionResult(null, $"深度为负值: {v}");
        return new ConversionResult(v * factor, null);
    }
}

/// <summary>年龄单位换算（ka / yr / a → ka；Ma→ka）。</summary>
public static class AgeConverter
{
    private static readonly Dictionary<string, double> ToKa = new(StringComparer.OrdinalIgnoreCase)
    {
        ["ka"] = 1.0, ["ka bp"] = 1.0, ["cal ka bp"] = 1.0, ["cal ka"] = 1.0,
        ["kyr"] = 1.0, ["ky"] = 1.0, ["a"] = 0.001, ["yr"] = 0.001, ["years"] = 0.001, ["year"] = 0.001,
        ["ma"] = 1000.0, ["myr"] = 1000.0,
    };

    public static ConversionResult Parse(string raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
            return new ConversionResult(null, "年龄为空");
        var s = raw.Trim();
        var unitStart = -1;
        string unit = "ka";
        foreach (var u in ToKa.Keys.OrderByDescending(k => k.Length))
        {
            var idx = s.ToLowerInvariant().IndexOf(u, StringComparison.OrdinalIgnoreCase);
            if (idx >= 0 && idx > 0) { unitStart = idx; unit = s[idx..].Trim(); break; }
        }
        var numPart = unitStart >= 0 ? s[..unitStart].Trim() : s;
        if (!double.TryParse(numPart, NumberStyles.Float, CultureInfo.InvariantCulture, out var v))
            return new ConversionResult(null, $"无法解析年龄数值: '{raw}'");
        if (!ToKa.TryGetValue(unit.ToLowerInvariant(), out var factor))
            return new ConversionResult(null, $"未知年龄单位: '{unit}'");
        return new ConversionResult(v * factor, null);
    }
}
