using System.Globalization;

namespace DepthAge.Domain;

/// <summary>
/// A measured quantity. The raw representation from the source is always preserved;
/// normalization to canonical units happens only in the derived layer.
/// Values that cannot be converted stay in place with a diagnostic (Value = null).
/// </summary>
public sealed record Measured
{
    /// <summary>Original text exactly as imported, e.g. "0.45 m" or "2450 14C yr BP".</summary>
    public required string Raw { get; init; }

    /// <summary>Unit token as written in the source (may be unrecognized).</summary>
    public required string Unit { get; init; }

    /// <summary>Normalized value in the canonical unit of the dimension, or null when unconvertible.</summary>
    public double? Value { get; init; }

    public static Measured Unconvertible(string raw, string unit) =>
        new() { Raw = raw, Unit = unit, Value = null };
}

public enum Dimension { Depth, CalendarAge, RadiocarbonAge, Duration }

/// <summary>
/// Unit conversion tables. Canonical units: depth = m (positive downward from datum),
/// calendar age = cal yr BP (BP = 1950 CE), radiocarbon age = 14C yr BP, duration = yr.
/// </summary>
public static class Units
{
    public const string DepthCanonical = "m";
    public const string CalAgeCanonical = "cal yr BP";
    public const string C14Canonical = "14C yr BP";
    public const string DurationCanonical = "yr";

    private static readonly Dictionary<string, (Dimension Dim, double Factor)> Table =
        new(StringComparer.OrdinalIgnoreCase)
        {
            ["m"] = (Dimension.Depth, 1.0),
            ["cm"] = (Dimension.Depth, 0.01),
            ["mm"] = (Dimension.Depth, 0.001),
            ["cmbsf"] = (Dimension.Depth, 0.01),
            ["mbsf"] = (Dimension.Depth, 1.0),
            ["cal yr BP"] = (Dimension.CalendarAge, 1.0),
            ["cal BP"] = (Dimension.CalendarAge, 1.0),
            ["cal ka BP"] = (Dimension.CalendarAge, 1000.0),
            ["yr BP"] = (Dimension.CalendarAge, 1.0),
            ["a BP"] = (Dimension.CalendarAge, 1.0),
            ["ka BP"] = (Dimension.CalendarAge, 1000.0),
            ["14C yr BP"] = (Dimension.RadiocarbonAge, 1.0),
            ["14C BP"] = (Dimension.RadiocarbonAge, 1.0),
            ["14C yr"] = (Dimension.RadiocarbonAge, 1.0),
            ["yr"] = (Dimension.Duration, 1.0),
            ["a"] = (Dimension.Duration, 1.0),
            ["ka"] = (Dimension.Duration, 1000.0),
        };

    public static bool TryNormalize(string unit, Dimension dimension, double value, out double canonical)
    {
        canonical = 0;
        if (!Table.TryGetValue(unit.Trim(), out var entry) || entry.Dim != dimension)
            return false;
        canonical = value * entry.Factor;
        return true;
    }

    public static bool IsKnownUnit(string unit) => Table.ContainsKey(unit.Trim());

    /// <summary>
    /// Parse a depth measurement like "0.45 m", "45 cm", "45 cmbsf". Returns null when the
    /// numeric part cannot be interpreted; returns an unconvertible Measured when the unit is
    /// unknown. Callers keep the raw value in place and attach a positioned diagnostic.
    /// </summary>
    public static Measured? ParseDepth(string raw)
    {
        var trimmed = raw.Trim();
        var split = trimmed.IndexOf(' ');
        var numberPart = split < 0 ? trimmed : trimmed[..split];
        var unitPart = split < 0 ? "m" : trimmed[(split + 1)..].Trim();
        if (!double.TryParse(numberPart, NumberStyles.Float, CultureInfo.InvariantCulture, out var value))
            return null;
        if (!TryNormalize(unitPart, Dimension.Depth, value, out var canonical))
            return Measured.Unconvertible(raw, unitPart);
        return new Measured { Raw = raw, Unit = unitPart, Value = canonical };
    }

    public static Measured Of(double value, string unit, Dimension dimension)
    {
        if (!TryNormalize(unit, dimension, value, out var canonical))
            return Measured.Unconvertible($"{value.ToString("G6", CultureInfo.InvariantCulture)} {unit}", unit);
        return new Measured
        {
            Raw = $"{value.ToString("G6", CultureInfo.InvariantCulture)} {unit}",
            Unit = unit,
            Value = canonical
        };
    }
}
