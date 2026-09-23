namespace AgeDepth.Core;

/// <summary>A physical quantity that always carries its unit and coordinate datum.</summary>
public sealed record Quantity(double Value, string Unit, string Datum)
{
    public static Quantity CoreDepth(double meters) => new(meters, "m", "core-top");
    public static Quantity CalAge(double ka) => new(ka, "ka", "calBP");

    public bool TryConvertTo(string unit, out Quantity converted)
    {
        // Depth conversions
        if (Unit == unit && Datum != "") { converted = this; return true; }
        if (Unit == "ft" && unit == "m") { converted = new Quantity(Value * 0.3048, "m", Datum); return true; }
        if (Unit == "cm" && unit == "m") { converted = new Quantity(Value / 100.0, "m", Datum); return true; }
        if (Unit == "ka" && unit == "ka") { converted = this with { Unit = "ka" }; return true; }
        converted = this;
        return false;
    }
}
