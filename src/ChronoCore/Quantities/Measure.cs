using System.Globalization;

namespace ChronoCore.Quantities;

/// <summary>
/// 一个领域量：原始表示（原样保留）+ 规范化后的 SI 值 + 量纲/基准。
/// 无法转换时 Canonical 为 null，诊断挂在 ConversionProblem 上，原始值仍留在原处。
/// </summary>
public sealed record Measure(
    string Raw,
    string Kind,
    double? Canonical,
    string CanonicalUnit,
    string Datum = null,
    string ConversionProblem = null)
{
    public static readonly IReadOnlyDictionary<string, double> LengthFactors = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase)
    {
        ["m"] = 1.0, ["cm"] = 0.01, ["mm"] = 0.001, ["ft"] = 0.3048
    };

    public static readonly IReadOnlyDictionary<string, double> AgeFactors = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase)
    {
        ["a"] = 1.0, ["yr"] = 1.0, ["yrs"] = 1.0, ["ka"] = 1000.0, ["kyr"] = 1000.0, ["ma"] = 1_000_000.0
    };

    public static Measure Length(double value, string unit, string datum = null)
        => new(Format(value) + unit, "length", value * RequireFactor(LengthFactors, unit), "m", datum);

    public static Measure Age(double value, string unit, string datum = null)
        => new(Format(value) + unit, "age", value * RequireFactor(AgeFactors, unit), "a", datum);

    public static Measure ParseLength(string raw, string datum = null)
        => Parse(raw, "length", LengthFactors, "m", datum);

    public static Measure ParseAge(string raw, string datum = null)
        => Parse(raw, "age", AgeFactors, "a", datum);

    public static Measure Parse(string raw, string kind, IReadOnlyDictionary<string, double> factors, string canonicalUnit, string datum)
    {
        var text = raw?.Trim();
        if (string.IsNullOrEmpty(text))
            return new Measure(raw, kind, null, canonicalUnit, datum, "empty-value");
        var unitIndex = FirstUnitIndex(text);
        var numText = text[..unitIndex].Trim();
        var unit = text[unitIndex..].Trim();
        if (!double.TryParse(numText, NumberStyles.Float, CultureInfo.InvariantCulture, out var v))
            return new Measure(raw, kind, null, canonicalUnit, datum, $"unparseable-number:{numText}");
        if (!factors.TryGetValue(unit, out var f))
            return new Measure(raw, kind, null, canonicalUnit, datum, $"unknown-unit:{unit}");
        return new Measure(raw, kind, v * f, canonicalUnit, datum);
    }

    private static double RequireFactor(IReadOnlyDictionary<string, double> factors, string unit)
        => factors.TryGetValue(unit, out var f) ? f : throw new ArgumentException($"unknown unit {unit}");

    private static int FirstUnitIndex(string s)
    {
        for (var i = 0; i < s.Length; i++)
            if (!(char.IsDigit(s[i]) || s[i] == '.' || s[i] == '-' || s[i] == '+' || s[i] == 'e' || s[i] == 'E'))
                return i;
        return s.Length;
    }

    private static string Format(double v) => v.ToString("0.######", CultureInfo.InvariantCulture);
}
