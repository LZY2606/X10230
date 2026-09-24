namespace DepthAge.Domain;

public static class Units
{
    public static readonly HashSet<string> KnownDepthDatums = new(StringComparer.OrdinalIgnoreCase)
    {
        "core-top", "mbsf"
    };

    public static bool TryDepthToMeters(double value, string unit, out double meters)
    {
        switch (unit.Trim().ToLowerInvariant())
        {
            case "m":
            case "meter":
            case "metre":
                meters = value;
                return true;
            case "cm":
                meters = value / 100.0;
                return true;
            case "mm":
                meters = value / 1000.0;
                return true;
            case "ft":
                meters = value * 0.3048;
                return true;
            default:
                meters = 0;
                return false;
        }
    }

    public static bool TryAgeToKa(double value, string unit, out double ka)
    {
        switch (unit.Trim().ToLowerInvariant())
        {
            case "ka":
            case "ka bp":
            case "kyr":
            case "kyr bp":
                ka = value;
                return true;
            case "yr":
            case "yr bp":
            case "a":
            case "cal yr bp":
                ka = value / 1000.0;
                return true;
            case "ma":
                ka = value * 1000.0;
                return true;
            default:
                ka = 0;
                return false;
        }
    }

    public static double Quantile(double[] sortedSamples, double q)
    {
        if (sortedSamples.Length == 0)
        {
            return double.NaN;
        }
        if (sortedSamples.Length == 1)
        {
            return sortedSamples[0];
        }
        var h = (sortedSamples.Length - 1) * q;
        var lo = (int)Math.Floor(h);
        var hi = Math.Min(lo + 1, sortedSamples.Length - 1);
        var frac = h - lo;
        return sortedSamples[lo] + (sortedSamples[hi] - sortedSamples[lo]) * frac;
    }
}
