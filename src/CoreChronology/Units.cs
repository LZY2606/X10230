using System.Globalization;

namespace CoreChronology;

/// <summary>
/// A domain quantity that always carries the representation as imported.
/// Normalisation to canonical units happens only on the derived layer;
/// values that cannot be converted stay in place with a diagnostic.
/// </summary>
public sealed record Measure(
    string Raw,
    string Unit,
    double? Canonical,
    string? CanonicalUnit,
    string? ConversionDiagnostic)
{
    public bool Converted => Canonical.HasValue && ConversionDiagnostic is null;
    public string OriginalRepresentation => $"{Raw} {Unit}".Trim();
}

/// <summary>Depth, age and reservoir-offset conversions. Never throws on bad input.</summary>
public static class UnitConverter
{
    private static readonly Dictionary<string, (double Factor, string Canonical)> DepthUnits = new()
    {
        ["m"] = (1.0, "m"), ["mbsf"] = (1.0, "m"), ["m b.s.f."] = (1.0, "m"),
        ["cm"] = (0.01, "m"), ["ft"] = (0.3048, "m"),
    };

    private static readonly Dictionary<string, (double Factor, string Canonical)> AgeUnits = new()
    {
        ["cal bp"] = (1.0, "cal yr BP"), ["cal yr bp"] = (1.0, "cal yr BP"),
        ["cal years bp"] = (1.0, "cal yr BP"), ["cal a bp"] = (1.0, "cal yr BP"),
        ["ka bp"] = (1000.0, "cal yr BP"), ["kyr bp"] = (1000.0, "cal yr BP"),
    };

    private static readonly Dictionary<string, (double Factor, string Canonical)> OffsetUnits = new()
    {
        ["yr"] = (1.0, "yr"), ["a"] = (1.0, "yr"), ["years"] = (1.0, "yr"),
        ["ka"] = (1000.0, "yr"),
    };

    public static Measure Parse(string raw, string? unit, string kind)
    {
        raw = (raw ?? string.Empty).Trim();
        unit = (unit ?? string.Empty).Trim();
        var table = kind switch
        {
            "depth" => DepthUnits,
            "age" => OffsetUnits, // handled below
            _ => OffsetUnits,
        };
        if (raw.Length == 0)
            return new Measure(raw, unit, null, null, "empty-value: value token is empty");
        if (unit.Length == 0)
            return new Measure(raw, unit, null, null, "missing-unit: no unit or coordinate basis declared");
        if (!double.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out var value))
            return new Measure(raw, unit, null, null, $"unparseable-value: '{raw}' is not numeric");

        if (kind == "age")
        {
            var key = unit.ToLowerInvariant();
            if (key is "14c bp" or "radiocarbon bp" or "c14 bp")
                return new Measure(raw, unit, null, null,
                    "unconvertible: conventional radiocarbon age requires a calibration curve; left at original position");
            if (AgeUnits.TryGetValue(key, out var a))
                return new Measure(raw, unit, value * a.Factor, a.Canonical, null);
            return new Measure(raw, unit, null, null, $"unknown-unit: '{unit}' is not a recognised age unit");
        }

        table = kind == "depth" ? DepthUnits : OffsetUnits;
        if (table.TryGetValue(unit.ToLowerInvariant(), out var t))
            return new Measure(raw, unit, value * t.Factor, t.Canonical, null);
        return new Measure(raw, unit, null, null, $"unknown-unit: '{unit}' is not a recognised {kind} unit");
    }
}
