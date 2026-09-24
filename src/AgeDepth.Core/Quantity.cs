namespace AgeDepth.Core;

/// <summary>
/// A domain quantity that always carries its unit and coordinate/time datum.
/// Raw representations are preserved on import; normalization happens only in
/// the derived layer. Values that cannot be converted stay in place and get a
/// diagnostic instead of being dropped.
/// </summary>
public sealed record Quantity(double Value, string Unit, string Datum)
{
    public string Display => $"{Value:0.###} {Unit} [{Datum}]";
}

public static class UnitConverter
{
    public const string CanonicalDepthUnit = "m";
    public const string CanonicalDepthDatum = "mbsf";
    public const string CanonicalAgeUnit = "ka";
    public const string CanonicalAgeDatum = "calBP";

    private static double DepthFactorToMeters(string unit) => unit switch
    {
        "m" => 1.0,
        "cm" => 0.01,
        "mm" => 0.001,
        _ => double.NaN
    };

    /// <summary>
    /// Convert a depth quantity to canonical meters below seafloor.
    /// Datum conversion (e.g. mcd -> mbsf) requires the offset published by a
    /// depth-datum material; unknown units/datums fail instead of guessing.
    /// </summary>
    public static bool TryConvertDepth(Quantity q, IReadOnlyDictionary<string, double> datumOffsetsToMbsf, out double metersMbsf)
    {
        metersMbsf = 0;
        var factor = DepthFactorToMeters(q.Unit);
        if (double.IsNaN(factor)) return false;
        var inMeters = q.Value * factor;
        if (q.Datum == CanonicalDepthDatum) { metersMbsf = inMeters; return true; }
        if (datumOffsetsToMbsf.TryGetValue(q.Datum, out var offset)) { metersMbsf = inMeters + offset; return true; }
        return false;
    }

    /// <summary>Scale factor that converts an age value into ka.</summary>
    public static bool TryAgeUnitFactor(string unit, out double factorToKa)
    {
        switch (unit)
        {
            case "ka": factorToKa = 1.0; return true;
            case "Ma": factorToKa = 1000.0; return true;
            case "yr":
            case "a": factorToKa = 0.001; return true;
            default: factorToKa = 0; return false;
        }
    }

    /// <summary>
    /// Convert an age value to canonical ka calBP. "BP1950" equals calBP;
    /// "CE" calendar years convert via 1950 - year.
    /// </summary>
    public static bool TryConvertAgeValue(double value, string unit, string datum, out double kaCalBP)
    {
        kaCalBP = 0;
        if (!TryAgeUnitFactor(unit, out var f)) return false;
        switch (datum)
        {
            case "calBP":
            case "BP1950":
                kaCalBP = value * f; return true;
            case "CE":
                var years = value * f * 1000.0; // value -> ka -> yr
                kaCalBP = (1950.0 - years) / 1000.0;
                return true;
            default:
                return false;
        }
    }
}
