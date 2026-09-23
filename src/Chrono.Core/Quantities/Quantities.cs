using System;
using System.Globalization;

namespace Chrono.Core.Quantities;

/// <summary>Depth direction convention. All stored normalized depths are positive downward.</summary>
public enum DepthDirection { PositiveDown = 0, PositiveUp = 1 }

/// <summary>
/// Depth relative to an explicit datum. Normalized value is always centimetres, positive downward.
/// Equality/compare use the normalized value so equivalent inputs compare equal regardless of unit text.
/// </summary>
public readonly record struct Depth(double Cm) : IComparable<Depth>
{
    public double Metres => Cm / 100.0;
    public int CompareTo(Depth other) => Cm.CompareTo(other.Cm);
    public static Depth operator +(Depth a, Depth b) => new(a.Cm + b.Cm);
    public static Depth operator -(Depth a, Depth b) => new(a.Cm - b.Cm);
    public override string ToString() => Cm.ToString("G", CultureInfo.InvariantCulture) + " cm";
}

/// <summary>How a raw depth string maps onto the normalized <see cref="Depth"/>.</summary>
public sealed record DepthReference(
    string Datum,
    string RawUnit,
    DepthDirection Direction,
    string? RawText = null);

public sealed record DepthWithReference(Depth Depth, DepthReference Reference);

/// <summary>Outcome of parsing one measurement: either a normalized value or a diagnostic at a source path.</summary>
public sealed record Normalization<T>(
    bool Ok,
    T? Value,
    string RawRepresentation,
    string UnitOrDatum,
    Diagnostic? Diagnostic) where T : struct
{
    public static Normalization<T> Success(T value, string raw, string unit) =>
        new(true, value, raw, unit, null);
    public static Normalization<T> Fail(string raw, string unit, Diagnostic d) =>
        new(false, null, raw, unit, d);
}

public sealed record Diagnostic(string Code, string Message, string Path)
{
    public override string ToString() => $"[{Code}] {Path}: {Message}";
}

/// <summary>Depth unit conversion. cm / m / mm are supported; anything else stays at the source.</summary>
public static class DepthUnits
{
    public static Normalization<Depth> Parse(string? rawText, string? rawUnit, string datum,
        DepthDirection direction, string path)
    {
        var raw = (rawText ?? "").Trim();
        var unit = (rawUnit ?? "").Trim().ToLowerInvariant();
        if (!double.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out var number))
            return Normalization<Depth>.Fail(raw, unit,
                new Diagnostic("DEPTH_NOT_NUMERIC", $"depth '{raw}' is not numeric", path));

        double? cm = unit switch
        {
            "cm" => number,
            "m" or "metre" or "meters" or "metre" => number * 100.0,
            "mm" => number / 10.0,
            _ => null
        };
        if (cm is null)
            return Normalization<Depth>.Fail(raw, unit,
                new Diagnostic("UNCONVERTIBLE_UNIT",
                    $"depth unit '{unit}' cannot be converted; value retained at source, not plotted", path));

        if (direction == DepthDirection.PositiveUp) cm = -cm.Value;
        return Normalization<Depth>.Success(new Depth(cm.Value), raw, unit);
    }
}

/// <summary>Calendar age. Normalized unit is cal yr BP relative to the named datum (normally 1950 CE).</summary>
public readonly record struct CalYearBp(double Years, string Datum = "BP1950") : IComparable<CalYearBp>
{
    public int CompareTo(CalYearBp other)
    {
        if (Datum != other.Datum)
            throw new InvalidOperationException($"cannot compare ages across datums {Datum} vs {other.Datum}");
        return Years.CompareTo(other.Years);
    }
    public override string ToString() => Years.ToString("G", CultureInfo.InvariantCulture) + $" cal yr {Datum}";
}
