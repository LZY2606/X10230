using System.Globalization;

namespace CoreChrono.Domain;

/// <summary>
/// A measured scalar carrying its original textual representation, unit, and datum.
/// Normalisation (to SI/canonical units) happens only on the derived layer; the
/// original text and unit are always preserved. A value that cannot be converted
/// stays in place with <see cref="Error"/> set and <see cref="Normalized"/> = null.
/// </summary>
public sealed record Measure
{
    public required string OriginalText { get; init; }
    public required string Unit { get; init; }
    public string? Datum { get; init; }

    /// <summary>Canonical value (metres for depth, calBP years for age, cm/ka for rate). Null iff unconvertible.</summary>
    public double? Normalized { get; init; }
    public string? Error { get; init; }

    public bool IsValid => Normalized is not null && Error is null;

    public static Measure Depth(string text, string unit, string? datum = null)
        => Create(text, unit, datum, Units.DepthFactors, canonicalUnit: "m");

    public static Measure Age(string text, string unit, string? datum = null)
        => Create(text, unit, datum, Units.AgeFactors, canonicalUnit: "calBP");

    public static Measure Rate(string text, string unit)
        => Create(text, unit, null, Units.RateFactors, canonicalUnit: "cm/ka");

    private static Measure Create(string text, string unit, string? datum,
        IReadOnlyDictionary<string, double> factors, string canonicalUnit)
    {
        var t = (text ?? string.Empty).Trim();
        if (!double.TryParse(t, NumberStyles.Float, CultureInfo.InvariantCulture, out var v))
        {
            return new Measure
            {
                OriginalText = text ?? string.Empty,
                Unit = unit ?? string.Empty,
                Datum = datum,
                Normalized = null,
                Error = $"无法解析数值: '{text}'"
            };
        }

        if (!factors.TryGetValue((unit ?? string.Empty).Trim().ToLowerInvariant(), out var f))
        {
            return new Measure
            {
                OriginalText = t,
                Unit = unit ?? string.Empty,
                Datum = datum,
                Normalized = null,
                Error = $"未知单位 '{unit}'（{canonicalUnit} 层可接受: {string.Join(", ", factors.Keys.OrderBy(k => k))}）"
            };
        }

        return new Measure
        {
            OriginalText = t,
            Unit = unit.Trim(),
            Datum = datum,
            Normalized = v * f
        };
    }

    public static double Require(Measure m, string path) =>
        m.Normalized ?? throw new FormatException($"字段 {path} 无法规范化: {m.Error}");

    public double RequireValue() => Normalized
        ?? throw new FormatException(Error ?? "值不可用");
}

public static class Units
{
    // factor multiplies the value into canonical units
    public static readonly IReadOnlyDictionary<string, double> DepthFactors =
        new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase)
        {
            ["m"] = 1.0, ["meter"] = 1.0, ["meters"] = 1.0, ["metre"] = 1.0, ["metres"] = 1.0,
            ["cm"] = 0.01,
            ["mm"] = 0.001,
            ["ft"] = 0.3048, ["feet"] = 0.3048,
        };

    public static readonly IReadOnlyDictionary<string, double> AgeFactors =
        new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase)
        {
            ["calbp"] = 1.0, ["cal bp"] = 1.0, ["calibrated bp"] = 1.0,
            ["bp"] = 1.0,
            ["b2k"] = -50.0, // B2K = years before 2000 CE; calBP = before 1950 => calBP = B2K - 50
            ["ka"] = 1000.0, ["cal ka bp"] = 1000.0,
            ["ma"] = 1_000_000.0,
        };

    public static readonly IReadOnlyDictionary<string, double> RateFactors =
        new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase)
        {
            ["cm/ka"] = 1.0, ["cm ka-1"] = 1.0, ["cm kyr-1"] = 1.0,
            ["m/ma"] = 0.1,   // 1 m/Ma = 100 cm / 1000 ka = 0.1 cm/ka
            ["mm/a"] = 1000.0, ["mm/yr"] = 1000.0,
            ["cm/a"] = 100_000.0, ["cm/yr"] = 100_000.0,
            ["m/ka"] = 100.0,
        };
}
