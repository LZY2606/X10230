using System.Globalization;

namespace Chrono.Core.Units;

public enum LengthUnit { Meter, Centimeter, Millimeter, Foot }
public enum TimeUnit { Year, CalendarYearBp }
public enum AgeScale { CalBp, Bp, BceCe }

public static class UnitConversions
{
    public static double ToMeters(double value, LengthUnit unit) => unit switch
    {
        LengthUnit.Meter => value,
        LengthUnit.Centimeter => value / 100.0,
        LengthUnit.Millimeter => value / 1000.0,
        LengthUnit.Foot => value * 0.3048,
        _ => throw new ArgumentOutOfRangeException(nameof(unit))
    };

    public static double FromMeters(double meters, LengthUnit unit) => unit switch
    {
        LengthUnit.Meter => meters,
        LengthUnit.Centimeter => meters * 100.0,
        LengthUnit.Millimeter => meters * 1000.0,
        LengthUnit.Foot => meters / 0.3048,
        _ => throw new ArgumentOutOfRangeException(nameof(unit))
    };

    public static string Symbol(LengthUnit unit) => unit switch
    {
        LengthUnit.Meter => "m",
        LengthUnit.Centimeter => "cm",
        LengthUnit.Millimeter => "mm",
        LengthUnit.Foot => "ft",
        _ => "?"
    };

    public static bool TryParseLengthUnit(string? text, out LengthUnit unit)
    {
        unit = default;
        if (string.IsNullOrWhiteSpace(text)) return false;
        switch (text.Trim().ToLowerInvariant())
        {
            case "m": case "meter": case "meters": unit = LengthUnit.Meter; return true;
            case "cm": case "centimeter": case "centimeters": unit = LengthUnit.Centimeter; return true;
            case "mm": case "millimeter": case "millimeters": unit = LengthUnit.Millimeter; return true;
            case "ft": case "foot": case "feet": unit = LengthUnit.Foot; return true;
            default: return false;
        }
    }
}

public readonly record struct DepthValue(double Meters)
{
    public static DepthValue From(double value, LengthUnit unit)
        => new(UnitConversions.ToMeters(value, unit));

    public double In(LengthUnit unit) => UnitConversions.FromMeters(Meters, unit);

    public string Format(LengthUnit unit)
        => In(unit).ToString("0.###", CultureInfo.InvariantCulture) + " " + UnitConversions.Symbol(unit);

    public static bool operator <(DepthValue a, DepthValue b) => a.Meters < b.Meters;
    public static bool operator >(DepthValue a, DepthValue b) => a.Meters > b.Meters;
}

public readonly record struct AgeValue(double CalYearBp)
{
    public AgeValue AddYears(double years) => new(CalYearBp + years);

    public string Format()
        => CalYearBp.ToString("0.#", CultureInfo.InvariantCulture) + " cal BP";
}

public enum VerticalDatumKind { CsfA, CsfB, Cmcd, Ccd, Mbsf, Custom }

public sealed record VerticalDatum(
    VerticalDatumKind Kind,
    string Code,
    string Name,
    double ShiftMeters,
    LengthUnit DisplayUnit,
    string? Note)
{
    public DepthValue ShiftToCoreTop(DepthValue value) => new(value.Meters + ShiftMeters);
    public DepthValue FromCoreTop(DepthValue value) => new(value.Meters - ShiftMeters);

    public static VerticalDatum Csfa =>
        new(VerticalDatumKind.CsfA, "CSF-A", "Composite Depth Scale A (core-top zero)", 0, LengthUnit.Meter, null);
}
