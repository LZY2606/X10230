using System.Globalization;

namespace StratAge.Core.Domain;

/// <summary>
/// A physical quantity that retains the original textual representation imported
/// from the source sheet. Canonical conversion happens only on the derived layer;
/// a value that cannot be converted stays in place with a diagnostic (NeverDrop).
/// </summary>
public sealed record QValue(string Raw, string RawUnit, double? Canonical, string? CanonicalUnit, string? ConversionDiagnostic)
{
    public bool IsConvertible => Canonical.HasValue;

    public static QValue Unconvertible(string raw, string rawUnit, string why) =>
        new(raw, rawUnit, null, null, why);

    public static QValue Of(double v, string unit) => new(v.ToString("R", CultureInfo.InvariantCulture), unit, v, unit, null);
}

/// <summary>Unit/coordinate conversion for depth and age domain quantities.</summary>
public static class Units
{
    // depth: canonical metres below sea floor (mbsf)
    static readonly Dictionary<string, double> DepthFactor = new(StringComparer.OrdinalIgnoreCase)
    {
        ["mbsf"] = 1.0, ["m"] = 1.0, ["mcd"] = 1.0,
        ["cm"] = 0.01, ["cmbsf"] = 0.01, ["mm"] = 0.001,
        ["ft"] = 0.3048, ["feet"] = 0.3048,
    };

    // age: canonical ka before present (ka BP)
    static readonly Dictionary<string, double> AgeFactor = new(StringComparer.OrdinalIgnoreCase)
    {
        ["ka bp"] = 1.0, ["ka"] = 1.0, ["kyr bp"] = 1.0, ["kyr"] = 1.0,
        ["ma bp"] = 1000.0, ["ma"] = 1000.0,
        ["yr bp"] = 0.001, ["years bp"] = 0.001, ["yr"] = 0.001, ["y bp"] = 0.001,
        ["bp"] = 0.001,
        ["cal ka bp"] = 1.0, ["calka"] = 1.0,
    };

    // rate: canonical m / ka (= cm/yr numerically *100; kept explicit)
    static readonly Dictionary<string, double> RateFactor = new(StringComparer.OrdinalIgnoreCase)
    {
        ["m/ka"] = 1.0, ["m ka-1"] = 1.0,
        ["cm/ka"] = 0.01, ["cm/yr"] = 10.0, ["cm/y"] = 10.0,
        ["mm/yr"] = 1.0, ["mm/y"] = 1.0,
    };

    public static QValue ConvertDepth(string raw, string? unit)
    {
        if (!double.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out var v))
            return QValue.Unconvertible(raw, unit ?? "", $"depth value '{raw}' is not numeric");
        if (unit is null || !DepthFactor.TryGetValue(unit.Trim(), out var f))
            return QValue.Unconvertible(raw, unit ?? "", $"unknown depth unit '{unit}' (known: mbsf, cm, mm, ft)");
        if (double.IsNaN(v) || double.IsInfinity(v) || v < 0)
            return QValue.Unconvertible(raw, unit, $"depth '{raw} {unit}' is outside the physical domain [0,+inf)");
        return new QValue(raw, unit, v * f, "mbsf", null);
    }

    public static QValue ConvertAge(string raw, string? unit)
    {
        if (!double.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out var v))
            return QValue.Unconvertible(raw, unit ?? "", $"age value '{raw}' is not numeric");
        if (unit is null || !AgeFactor.TryParseKey(unit.Trim(), out var f))
            return QValue.Unconvertible(raw, unit ?? "", $"unknown age datum/unit '{unit}' (known: ka BP, ma BP, yr BP, cal ka BP)");
        if (double.IsNaN(v) || double.IsInfinity(v) || v < 0)
            return QValue.Unconvertible(raw, unit, $"age '{raw} {unit}' is outside the physical domain [0,+inf)");
        return new QValue(raw, unit, v * f, "ka BP", null);
    }

    public static QValue ConvertRate(string raw, string? unit)
    {
        if (!double.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out var v))
            return QValue.Unconvertible(raw, unit ?? "", $"rate value '{raw}' is not numeric");
        if (unit is null || !RateFactor.TryParseKey(unit.Trim(), out var f))
            return QValue.Unconvertible(raw, unit ?? "", $"unknown rate unit '{unit}' (known: m/ka, cm/ka, cm/yr, mm/yr)");
        return new QValue(raw, unit, v * f, "m/ka", null);
    }

    public static string Fmt(double x) => x.ToString("0.######", CultureInfo.InvariantCulture);
}

internal static class DictExt
{
    public static bool TryParseKey<T>(this Dictionary<string, T> d, string key, out T value)
    {
        foreach (var (k, v) in d)
            if (string.Equals(k, key, StringComparison.OrdinalIgnoreCase)) { value = v!; return true; }
        value = default!;
        return false;
    }
}
