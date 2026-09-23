using System.Globalization;

namespace ChronoCore;

/// <summary>
/// A measured quantity that always retains its raw textual representation. Conversion to the
/// canonical internal unit is best-effort; an unconvertible value stays in place with a diagnostic
/// instead of being silently dropped or replaced by a default.
/// </summary>
public sealed record Quantity(
    string Raw,
    string Kind,
    double? Value,
    string? CanonicalUnit,
    string? OriginalUnit,
    string? Basis,
    string? DiagnosticCode = null,
    string? Diagnostic = null)
{
    public bool IsConvertible => Value.HasValue;
    public string Display => Value.HasValue
        ? Value.Value.ToString("0.######", CultureInfo.InvariantCulture) + " " + CanonicalUnit
        : Raw + " (未转换)";

    public static Quantity Depth(string raw, string datumId) => QuantityParser.Parse(raw, MeasureKind.Depth, datumId);
    public static Quantity Age(string raw) => QuantityParser.Parse(raw, MeasureKind.Age, null);
    public static Quantity Offset(string raw) => QuantityParser.Parse(raw, MeasureKind.Offset, null);
    public static Quantity Rate(string raw) => QuantityParser.Parse(raw, MeasureKind.Rate, null);
}

public enum MeasureKind { Depth, Age, Rate, Offset }

public static class QuantityParser
{
    // canonical units: depth -> m, age -> a BP, rate -> m/a, offset -> a
    public static Quantity Parse(string raw, MeasureKind kind, string? datumId)
    {
        var r = (raw ?? string.Empty).Trim();
        if (r.Length == 0)
            return Fail(raw, Label(kind), "Q-EMPTY", "空测量值，保留原位，不使用默认值");
        var m = System.Text.RegularExpressions.Regex.Match(
            r, @"^\s*([-+]?[0-9]*\.?[0-9]+(?:[eE][-+]?[0-9]+)?)\s*([^0-9].*?)?\s*$");
        if (!m.Success)
            return Fail(raw, Label(kind), "Q-NOTNUMERIC", "无法解析数值与单位，原始表示保留");
        if (!double.TryParse(m.Groups[1].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out var num))
            return Fail(raw, Label(kind), "Q-NOTNUMERIC", "数值超出可解析范围");
        var unit = (m.Groups[2].Success ? m.Groups[2].Value : string.Empty).Trim();

        return kind switch
        {
            MeasureKind.Depth => Depth(raw, num, unit, datumId),
            MeasureKind.Age => Age(raw, num, unit),
            MeasureKind.Offset => OffsetQ(raw, num, unit),
            MeasureKind.Rate => Rate(raw, num, unit),
            _ => Fail(raw, Label(kind), "Q-KIND", "未知量纲")
        };
    }

    private static string Label(MeasureKind k) => k switch
    {
        MeasureKind.Depth => "Depth", MeasureKind.Age => "Age",
        MeasureKind.Rate => "Rate", _ => "Offset"
    };

    private static Quantity Fail(string raw, string kind, string code, string msg)
        => new(raw, kind, null, null, null, null, code, msg);

    private static Quantity Depth(string raw, double num, string unit, string? datumId)
    {
        var basis = string.IsNullOrEmpty(datumId) ? "未声明深度基准" : datumId;
        return unit.ToLowerInvariant() switch
        {
            "mbsf" or "m bsf" or "m" => new(raw, "Depth", num, "m", unit, basis),
            "cmbsf" or "cm bsf" or "cm" => new(raw, "Depth", num / 100.0, "m", unit, basis),
            "mcd" => new(raw, "Depth", num, "m", unit, "MCD(复合深度)"),
            var u when u.StartsWith("?") || u.Contains("???") =>
                Fail(raw, "Depth", "Q-UNKNOWN-UNIT", $"未知深度单位 '{unit}'，无法转换为基准米，原值保留"),
            _ => Fail(raw, "Depth", "Q-UNKNOWN-UNIT", $"未知深度单位 '{unit}'，无法转换为基准米，原值保留")
        };
    }

    private static Quantity Age(string raw, double num, string unit)
    {
        var u = unit.ToLowerInvariant();
        if (u.Contains("14c") || u.Contains("radiocarbon") || u.Contains("bp(14c)"))
            return Fail(raw, "Age", "Q-RADIOCARBON-RAW",
                "常规放射性碳年龄(14C a BP)未经树轮校正，不能直接换算为日历年，原始值保留待校正分布");
        if (u.StartsWith("cal ") && (u.Contains("ka bp")))
            return new(raw, "Age", num * 1000.0, "a BP", unit, "cal BP(日历校正后)");
        if (u.Contains("ka bp"))
            return new(raw, "Age", num * 1000.0, "a BP", unit, "ka BP(未注明校正，按千年原值规范化，需来源确认)");
        if (u.StartsWith("cal"))
            return new(raw, "Age", num, "a BP", unit, "cal BP(日历校正后)");
        return u switch
        {
            "a bp" or "yr bp" or "a" or "yr" or "years bp" =>
                new(raw, "Age", num, "a BP", unit, "BP(未注明校正)"),
            _ => Fail(raw, "Age", "Q-UNKNOWN-UNIT", $"未知年龄单位 '{unit}'，原值保留")
        };
    }

    private static Quantity OffsetQ(string raw, double num, string unit)
    {
        return unit.ToLowerInvariant() switch
        {
            "a" or "yr" or "years" or "cal a" or "cal a bp" =>
                new(raw, "Offset", num, "a", unit, "储层校正量(日历年)"),
            _ => Fail(raw, "Offset", "Q-UNKNOWN-UNIT", $"未知偏移单位 '{unit}'，原值保留")
        };
    }

    private static Quantity Rate(string raw, double num, string unit)
    {
        var u = unit.ToLowerInvariant().Replace(" ", "");
        return u switch
        {
            "cm/ka" => new(raw, "Rate", num * 1e-5, "m/a", unit, "沉积速率"),
            "m/ma" => new(raw, "Rate", num * 1e-6, "m/a", unit, "沉积速率"),
            "a/m" => num == 0
                ? Fail(raw, "Rate", "Q-ZERO-DIV", "零深度间隔，速率无定义")
                : new(raw, "Rate", 1.0 / num, "m/a", unit, "年龄/深度倒数换算"),
            _ => Fail(raw, "Rate", "Q-UNKNOWN-UNIT", $"未知速率单位 '{unit}'，原值保留")
        };
    }
}
