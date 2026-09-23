namespace AgeDepth.Core;

/// <summary>
/// A domain quantity. The raw representation captured at import is always
/// preserved; normalization happens only in the derived layer. Values that
/// cannot be converted stay in place and carry a diagnostic instead of being
/// silently dropped or defaulted.
/// </summary>
public sealed record Quantity(
    double Value,
    string Unit,
    string Basis,
    string RawValue,
    string RawUnit)
{
    public static Quantity Of(double value, string unit, string basis) =>
        new(value, unit, basis, value.ToString("G17", System.Globalization.CultureInfo.InvariantCulture), unit);
}

public static class UnitConversion
{
    /// <summary>Supported depth normalizations, all into the datum basis metre.</summary>
    public static bool TryToMetres(Quantity q, out double metres, out string? error)
    {
        switch (q.Unit)
        {
            case "m": metres = q.Value; error = null; return true;
            case "cm": metres = q.Value / 100.0; error = null; return true;
            case "mm": metres = q.Value / 1000.0; error = null; return true;
            case "ft": metres = q.Value * 0.3048; error = null; return true;
            default:
                metres = double.NaN;
                error = $"unit '{q.Unit}' cannot be converted to metres";
                return false;
        }
    }

    /// <summary>Supported age normalizations, all into years BP (1950).</summary>
    public static bool TryToYearsBp(Quantity q, out double yearsBp, out string? error)
    {
        switch (q.Unit)
        {
            case "yr BP": yearsBp = q.Value; error = null; return true;
            case "kyr BP": yearsBp = q.Value * 1000.0; error = null; return true;
            case "cal yr BP": yearsBp = q.Value; error = null; return true;
            default:
                yearsBp = double.NaN;
                error = $"unit '{q.Unit}' cannot be converted to yr BP";
                return false;
        }
    }
}
