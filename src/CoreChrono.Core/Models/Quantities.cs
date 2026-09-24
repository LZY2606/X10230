using System.Globalization;
using System.Text.Json.Serialization;

namespace CoreChrono.Core.Models;

/// <summary>
/// A measured quantity that always carries the raw textual representation plus the
/// unit/datum exactly as imported. Normalization to an internal canonical unit happens
/// only on the derived layer; values that cannot be converted stay in place with a
/// diagnostic instead of being dropped or silently defaulted.
/// </summary>
public sealed record Quantity
{
    public string Raw { get; init; } = "";
    public string Unit { get; init; } = "";
    public double? Value { get; init; }

    [JsonIgnore]
    public bool HasCanonicalValue => Value.HasValue;

    public static Quantity Parse(string raw, string unit)
    {
        var normalizedUnit = (unit ?? "").Trim();
        var text = (raw ?? "").Trim();
        if (text.Length == 0)
            return new Quantity { Raw = raw ?? "", Unit = normalizedUnit };

        // Numeric part may carry a leading sign and an exponent; a trailing unit token
        // inside Raw ("12.3 cm") is tolerated but the declared Unit stays authoritative.
        var token = text;
        var space = token.IndexOf(' ');
        if (space > 0) token = token[..space];

        if (double.TryParse(token, NumberStyles.Float, CultureInfo.InvariantCulture, out var v) &&
            UnitConversions.IsKnownUnit(normalizedUnit))
        {
            return new Quantity
            {
                Raw = raw ?? "",
                Unit = normalizedUnit,
                Value = UnitConversions.ToCanonical(v, normalizedUnit)
            };
        }
        return new Quantity { Raw = raw ?? "", Unit = normalizedUnit };
    }
}

/// <summary>Canonical units: depth = metres below datum, age = years BP-like scale (yr).</summary>
public static class UnitConversions
{
    public static readonly IReadOnlyDictionary<string, double> LengthToMetres = new Dictionary<string, string>
    {
        ["m"] = "1", ["metre"] = "1", ["metres"] = "1", ["meter"] = "1", ["meters"] = "1",
        ["cm"] = "0.01", ["mm"] = "0.001"
    }.ToDictionary(kv => kv.Key, kv => double.Parse(kv.Value, CultureInfo.InvariantCulture));

    public static readonly IReadOnlyDictionary<string, double> AgeToYears = new Dictionary<string, string>
    {
        ["yr"] = "1", ["yrs"] = "1", ["year"] = "1", ["years"] = "1", ["a"] = "1", ["ka"] = "1000",
        ["kyr"] = "1000", ["ky"] = "1000", ["ma"] = "1000000", ["myr"] = "1000000"
    }.ToDictionary(kv => kv.Key, kv => double.Parse(kv.Value, CultureInfo.InvariantCulture));

    public static bool IsKnownUnit(string unit) =>
        LengthToMetres.ContainsKey(unit) || AgeToYears.ContainsKey(unit);

    public static bool IsLength(string unit) => LengthToMetres.ContainsKey(unit);
    public static bool IsAge(string unit) => AgeToYears.ContainsKey(unit);

    public static double ToCanonical(double value, string unit)
    {
        if (LengthToMetres.TryGetValue(unit, out var lf)) return value * lf;
        if (AgeToYears.TryGetValue(unit, out var af)) return value * af;
        throw new ArgumentException($"Unknown unit '{unit}'");
    }

    public static string DimensionOf(string unit) =>
        IsLength(unit) ? "length" : IsAge(unit) ? "age" : "unknown";
}
