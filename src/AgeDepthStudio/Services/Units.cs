using AgeDepthStudio.Domain;

namespace AgeDepthStudio.Services;

/// <summary>
/// Unit and datum normalization. Raw representations are never overwritten;
/// values that cannot be converted stay in place and carry a diagnostic.
/// </summary>
public static class Units
{
    public static double? ToMeters(double value, string unit)
        => unit.Trim().ToLowerInvariant() switch
        {
            "m" or "meter" or "meters" => value,
            "cm" => value / 100.0,
            "mm" => value / 1000.0,
            _ => null
        };

    public static double? ToKa(double value, string unit)
        => unit.Trim().ToLowerInvariant() switch
        {
            "ka" or "ka bp" or "cal ka bp" or "calkabp" => value,
            "yr" or "yr bp" or "a bp" => value / 1000.0,
            _ => null
        };

    public static bool TryParseDepth(string raw, string unit, out double meters, out Diagnostic? diag)
    {
        meters = 0;
        diag = null;
        if (!double.TryParse(raw, System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture, out var v))
        {
            diag = new Diagnostic("depth.unparseable", "", $"depth value '{raw}' is not a number");
            return false;
        }
        var m = ToMeters(v, unit);
        if (m is null)
        {
            diag = new Diagnostic("depth.unit-unknown", "",
                $"depth unit '{unit}' has no configured conversion; value left in place");
            return false;
        }
        meters = m.Value;
        return true;
    }
}
