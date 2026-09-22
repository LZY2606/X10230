namespace Chrono.Core.Model;

/// <summary>
/// Every domain quantity carries its unit/reference frame. The raw representation is preserved on
/// import; only derived views normalize units. Values that cannot be converted stay in place with a
/// diagnostic instead of being dropped for plotting.
/// </summary>
public enum LengthUnit { Metres, Centimetres, Millimetres, Feet }
public enum AgeUnit { KaBp, YearBp, YearBceCe, CalKaBp }

public readonly record struct Depth(double Value, LengthUnit Unit)
{
    public double Metres => Unit switch
    {
        LengthUnit.Metres => Value,
        LengthUnit.Centimetres => Value / 100.0,
        LengthUnit.Millimetres => Value / 1000.0,
        LengthUnit.Feet => Value * 0.3048,
        _ => double.NaN,
    };

    public string Canonical => $"{Metres:R}m";
    public override string ToString() => $"{Value:R} {UnitToken}";
    public string UnitToken => Unit switch
    {
        LengthUnit.Metres => "m", LengthUnit.Centimetres => "cm",
        LengthUnit.Millimetres => "mm", LengthUnit.Feet => "ft", _ => "?"
    };

    public static LengthUnit? ParseUnit(string? token) => (token ?? "").Trim().ToLowerInvariant() switch
    {
        "m" or "metre" or "metres" or "meter" => LengthUnit.Metres,
        "cm" => LengthUnit.Centimetres,
        "mm" => LengthUnit.Millimetres,
        "ft" or "feet" => LengthUnit.Feet,
        _ => null,
    };
}

/// <summary>Depth datum/reference frame of a core (e.g. metres below sea floor).</summary>
public sealed record DepthDatum(string Id, string RawText, string ReferenceFrame, double TopMetres = 0.0);

/// <summary>A scalar age with its time reference; all internal derived work uses ka BP.</summary>
public readonly record struct Age(double KaBp, AgeUnit Unit)
{
    public double In(AgeUnit u) => u == AgeUnit.KaBp || u == AgeUnit.CalKaBp ? KaBp : KaBp * 1000.0;
}
