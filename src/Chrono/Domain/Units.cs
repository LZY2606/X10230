using System.Globalization;

namespace Chrono.Domain;

/// <summary>
/// 领域量值：同时保留原始文本表示、规范化数值与转换诊断。
/// 无法转换时 Value 为 null，但 Raw 与诊断永远保留，不会为了画图而丢弃。
/// </summary>
public sealed record QValue(string Raw, double? Value, string? Unit, string? Diagnostic, string? SourceRef)
{
    public bool IsConverted => Value.HasValue;

    public static QValue Ok(double value, string? unit, string raw, string? sourceRef = null)
        => new(raw, value, unit, null, sourceRef);

    public static QValue Fail(string raw, string diagnostic, string? sourceRef = null, string? unit = null)
        => new(raw, null, unit, diagnostic, sourceRef);

    public string Display => Value.HasValue
        ? string.Create(CultureInfo.InvariantCulture, $"{Value.Value:G6} {Unit}".TrimEnd())
        : $"{Raw} ⚠ {Diagnostic}";
}

public enum LengthUnit { Cm, M, Mbsf, Mm, Ft }

public static class LengthUnits
{
    public static readonly Dictionary<string, LengthUnit> Aliases = new(StringComparer.OrdinalIgnoreCase)
    {
        ["cm"] = LengthUnit.Cm, ["m"] = LengthUnit.M, ["mbsf"] = LengthUnit.Mbsf,
        ["mm"] = LengthUnit.Mm, ["ft"] = LengthUnit.Ft, ["feet"] = LengthUnit.Ft,
    };

    /// <summary>换算为米（mbsf 视作米，是深度基准下的常用记号）。</summary>
    public static double ToMetres(double value, LengthUnit unit) => unit switch
    {
        LengthUnit.Cm => value / 100.0,
        LengthUnit.M => value,
        LengthUnit.Mbsf => value,
        LengthUnit.Mm => value / 1000.0,
        LengthUnit.Ft => value * 0.3048,
        _ => throw new ArgumentOutOfRangeException(nameof(unit)),
    };

    public static bool TryParse(string? text, out LengthUnit unit)
    {
        unit = LengthUnit.M;
        if (string.IsNullOrWhiteSpace(text)) return false;
        return Aliases.TryGetValue(text.Trim(), out unit);
    }
}

public enum AgeUnit { Bp, CalBp, YrBp, Ka }

public static class AgeUnits
{
    public static readonly Dictionary<string, AgeUnit> Aliases = new(StringComparer.OrdinalIgnoreCase)
    {
        ["bp"] = AgeUnit.Bp, ["yr bp"] = AgeUnit.YrBp, ["yrbp"] = AgeUnit.YrBp,
        ["cal bp"] = AgeUnit.CalBp, ["calbp"] = AgeUnit.CalBp, ["cal. bp"] = AgeUnit.CalBp,
        ["ka"] = AgeUnit.Ka, ["ka bp"] = AgeUnit.Ka,
    };

    /// <summary>规范化为 cal BP（年）。未校准 BP 不做自动转换，由校准步骤处理。</summary>
    public static double ToCalBpYears(double value, AgeUnit unit) => unit switch
    {
        AgeUnit.CalBp => value,
        AgeUnit.YrBp => value,
        AgeUnit.Bp => value,
        AgeUnit.Ka => value * 1000.0,
        _ => throw new ArgumentOutOfRangeException(nameof(unit)),
    };

    public static bool TryParse(string? text, out AgeUnit unit)
    {
        unit = AgeUnit.CalBp;
        if (string.IsNullOrWhiteSpace(text)) return false;
        return Aliases.TryParse(Normalize(text), out unit);
    }

    public static string Normalize(string text) =>
        text.Trim().ToLowerInvariant().Replace(".", "").Replace("  ", " ");
}

/// <summary>数值解析（固定不变文化，保留原始文本）。</summary>
public static class Numbers
{
    public static double? TryParse(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return null;
        if (double.TryParse(raw.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out var v) &&
            double.IsFinite(v))
            return v;
        return null;
    }
}
