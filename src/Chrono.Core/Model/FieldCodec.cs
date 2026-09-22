using System.Globalization;

namespace Chrono.Core.Model;

/// <summary>
/// Parses domain fields while preserving the raw representation. Unconvertible values produce a
/// located diagnostic instead of an exception — they stay in place and are never dropped.
/// </summary>
public static class FieldCodec
{
    public static (double? Metres, string? Reason) ParseDepth(string raw, string? unitToken)
    {
        var unit = Depth.ParseUnit(unitToken);
        if (unit is null)
            return (null, $"unknown depth unit '{unitToken}' (raw='{raw}')");
        if (!double.TryParse(raw.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out var v))
            return (null, $"depth not numeric (raw='{raw}' {unitToken})");
        if (double.IsNaN(v) || double.IsInfinity(v))
            return (null, $"depth not finite (raw='{raw}' {unitToken})");
        return (new Depth(v, unit.Value).Metres, null);
    }

    public static (AgeDistribution? Distribution, string? Reason) ParseAge(string raw)
    {
        var text = raw.Trim();
        try
        {
            // gaussian:  "12.4±0.3 ka BP"  or  "12.4 +- 0.3 ka"
            var m = System.Text.RegularExpressions.Regex.Match(
                text, @"^(?<mean>-?\d+(\.\d+)?)\s*(?:±|\+\/-|\+-)\s*(?<sig>\d+(\.\d+)?)\s*(?<unit>ka|cal\s?ka|yr?|a)\s*bp?$",
                System.Text.RegularExpressions.RegexOptions.IgnoreCase);
            if (m.Success)
            {
                double mean = ScaleToKa(double.Parse(m.Groups["mean"].Value, CultureInfo.InvariantCulture), m.Groups["unit"].Value);
                double sig = ScaleToKa(double.Parse(m.Groups["sig"].Value, CultureInfo.InvariantCulture), m.Groups["unit"].Value);
                return (new GaussianAge(mean, sig, text), null);
            }

            // uniform: "[11.0, 11.8] ka BP"
            m = System.Text.RegularExpressions.Regex.Match(
                text, @"^\[\s*(?<lo>-?\d+(\.\d+)?)\s*,\s*(?<hi>-?\d+(\.\d+)?)\s*\]\s*(?<unit>ka|cal\s?ka|yr?|a)\s*bp?$",
                System.Text.RegularExpressions.RegexOptions.IgnoreCase);
            if (m.Success)
            {
                double lo = ScaleToKa(double.Parse(m.Groups["lo"].Value, CultureInfo.InvariantCulture), m.Groups["unit"].Value);
                double hi = ScaleToKa(double.Parse(m.Groups["hi"].Value, CultureInfo.InvariantCulture), m.Groups["unit"].Value);
                if (hi <= lo) return (null, $"uniform age low>=high (raw='{text}')");
                return (new UniformAge(lo, hi, text), null);
            }

            // empirical: "samples(ka BP)=10.1,10.4,11.9;w=5,3,1"
            m = System.Text.RegularExpressions.Regex.Match(
                text, @"^samples\((?<unit>ka|yr?|a)\s*bp?\)\s*=\s*(?<vals>[\d.,\s-]+);\s*w\s*=\s*(?<w>[\d.,\s]+)$",
                System.Text.RegularExpressions.RegexOptions.IgnoreCase);
            if (m.Success)
            {
                var vals = m.Groups["vals"].Value.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
                    .Select(s => ScaleToKa(double.Parse(s, CultureInfo.InvariantCulture), m.Groups["unit"].Value)).ToList();
                var ws = m.Groups["w"].Value.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
                    .Select(s => double.Parse(s, CultureInfo.InvariantCulture)).ToList();
                if (vals.Count != ws.Count || vals.Count < 2)
                    return (null, $"empirical samples/weights mismatch (raw='{text}')");
                if (ws.Any(w => w < 0) || ws.All(w => w == 0))
                    return (null, $"empirical weights invalid (raw='{text}')");
                var pairs = vals.Zip(ws).OrderBy(p => p.First).ToList();
                return (new EmpiricalAge(pairs.Select(p => p.First).ToList(), pairs.Select(p => p.Second).ToList(), text), null);
            }

            return (null, $"unrecognized age distribution (raw='{text}')");
        }
        catch (Exception ex)
        {
            return (null, $"age parse failed: {ex.Message} (raw='{text}')");
        }
    }

    private static double ScaleToKa(double v, string unit)
    {
        var u = unit.Replace(" ", "").ToLowerInvariant();
        return (u is "yr" or "y" or "a" or "yrbp" or "abp") ? v / 1000.0 : v;
    }
}
