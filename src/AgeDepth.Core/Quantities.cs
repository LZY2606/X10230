using System.Globalization;
using System.Text.RegularExpressions;

namespace AgeDepth.Core;

/// <summary>
/// A value as it appeared in the source material. The raw representation is always
/// retained; the derived layer normalizes to canonical units (depth to meters below
/// datum, age to cal yr BP). Values that cannot be converted stay in place with a
/// diagnostic instead of being dropped or replaced by a default.
/// </summary>
public sealed record Quantity(double? Value, string Unit, string? Datum, string Raw)
{
    public static readonly Quantity Missing = new(null, "", null, "");

    public bool IsMissing => Value is null;

    public static Quantity Parse(string? raw, string field, string ownerId, List<Diagnostic> diags)
    {
        var original = raw?.Trim() ?? "";
        if (original.Length == 0)
        {
            diags.Add(Diagnostic.New("VALUE_MISSING", DiagnosticSeverity.Error, ownerId, field,
                "原始值缺失，已保留原位，不使用空值或默认值替代。", 0));
            return Missing with { Raw = original };
        }

        var match = Regex.Match(original,
            "^(?<num>[+-]?(?:\\d+\\.\\d+|\\d+|\\.\\d+)(?:[eE][+-]?\\d+)?)\\s*(?<unit>[A-Za-z%/\\u00b5][A-Za-z0-9%/\\. ]*?)\\s*$");
        if (!match.Success)
        {
            diags.Add(Diagnostic.New("VALUE_UNPARSEABLE", DiagnosticSeverity.Error, ownerId, field,
                "无法解析原始表示，保留在原位置: " + original, 0));
            return new Quantity(null, "", null, original);
        }

        var number = double.Parse(match.Groups["num"].Value, CultureInfo.InvariantCulture);
        var unit = match.Groups["unit"].Value.Trim();
        return new Quantity(number, unit, null, original);
    }

    public static Quantity Explicit(double value, string unit, string raw) =>
        new(value, unit, null, raw);
}

/// <summary>Unit conversion helpers for the derived (normalization) layer.</summary>
public static class Units
{
    /// <summary>Canonical depth unit: meters below the referenced datum.</summary>
    public static bool TryDepthMeters(Quantity q, out double meters, out string? error)
    {
        meters = 0;
        error = null;
        if (q.IsMissing) { error = "深度缺失"; return false; }
        switch (q.Unit.ToLowerInvariant())
        {
            case "m": case "mbsf": case "m bsf": case "m below seafloor":
                meters = q.Value!.Value; break;
            case "cm": meters = q.Value!.Value / 100.0; break;
            case "mm": meters = q.Value!.Value / 1000.0; break;
            case "ft": case "feet": meters = q.Value!.Value * 0.3048; break;
            default: error = "未知深度单位，值保留原位: " + q.Raw; return false;
        }
        return true;
    }

    /// <summary>Canonical age unit: calendar years BP (before 1950 CE).</summary>
    public static bool TryAgeCalBp(Quantity q, out double calBp, out string? error)
    {
        calBp = 0;
        error = null;
        if (q.IsMissing) { error = "年龄缺失"; return false; }
        var u = q.Unit.ToLowerInvariant();
        if (u is "ka bp" or "ka" or "kya" or "kyr bp") { calBp = q.Value!.Value * 1000.0; return true; }
        if (u is "cal bp" or "bp" or "yr bp" or "yr" or "a bp" or "years bp" or "cal yr bp")
        { calBp = q.Value!.Value; return true; }
        if (u is "ce" or "ad") { calBp = 1950.0 - q.Value!.Value; return true; }
        if (u is "bce" or "bc") { calBp = 1950.0 + q.Value!.Value; return true; }
        error = "未知年龄单位/时间基准，值保留原位: " + q.Raw;
        return false;
    }

    public static string FormatDepthMeters(double m) =>
        (m * 100.0).ToString("0.##", CultureInfo.InvariantCulture) + " cm";

    public static string FormatAgeCalBp(double calBp)
    {
        if (Math.Abs(calBp) >= 10000)
            return (calBp / 1000.0).ToString("0.##", CultureInfo.InvariantCulture) + " ka cal BP";
        return calBp.ToString("0.#", CultureInfo.InvariantCulture) + " cal yr BP";
    }

    public static string FormatRateMetersPerKa(double mPerKa)
    {
        var cmPerKa = mPerKa * 100.0;
        if (cmPerKa >= 100)
            return mPerKa.ToString("0.###", CultureInfo.InvariantCulture) + " m/ka";
        return cmPerKa.ToString("0.##", CultureInfo.InvariantCulture) + " cm/ka";
    }
}
