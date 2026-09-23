using System.Globalization;
using System.Text.RegularExpressions;
using Chrono.Core.Model;

namespace Chrono.Core.Normalization;

/// <summary>
/// Normalizes domain quantities for derived layers only. Raw text is always retained
/// on the source facts. Unknown units/datums produce a diagnostic rather than a guess.
/// </summary>
public static partial class Units
{
    private static readonly HashSet<string> DepthDatums =
        new(StringComparer.OrdinalIgnoreCase) { "MBSF", "CMBSF", "MCD", "M LAT" };

    public static Measure ParseDepth(string? raw, string? materialId, string field)
    {
        if (string.IsNullOrWhiteSpace(raw))
            return Fail(raw ?? "", "DEPTH_EMPTY", "depth is empty", materialId, field, null);

        var m = TokenRegex().Match(raw.Trim());
        if (!m.Success)
            return Fail(raw, "DEPTH_UNPARSEABLE", $"cannot parse depth '{raw}'", materialId, field, raw);

        if (!double.TryParse(m.Groups["num"].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out var n))
            return Fail(raw, "DEPTH_UNPARSEABLE", $"bad number in '{raw}'", materialId, field, raw);

        var unit = m.Groups["unit"].Value.Trim().ToLowerInvariant();
        var rest = m.Groups["rest"].Value.Trim();
        string? datum = null;
        if (!string.IsNullOrEmpty(rest))
        {
            datum = rest.ToUpperInvariant();
            if (!DepthDatums.Contains(datum))
                return Fail(raw, "DATUM_UNKNOWN", $"unknown vertical datum '{rest}'", materialId, field, raw);
        }

        var meters = unit switch
        {
            "m" or "meter" or "meters" => n,
            "cm" => n / 100.0,
            "mm" => n / 1000.0,
            _ => null
        };
        if (meters is null)
            return Fail(raw, "UNIT_UNKNOWN", $"unsupported depth unit '{unit}'", materialId, field, raw);
        return new Measure(raw, meters, "m", datum, null, null);
    }

    public static Measure ParseCalYearsBP(string? raw, string? materialId, string field)
    {
        if (string.IsNullOrWhiteSpace(raw))
            return Fail(raw ?? "", "AGE_EMPTY", "age is empty", materialId, field, null);
        var m = TokenRegex().Match(raw.Trim());
        if (!m.Success)
            return Fail(raw, "AGE_UNPARSEABLE", $"cannot parse age '{raw}'", materialId, field, raw);
        if (!double.TryParse(m.Groups["num"].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out var n))
            return Fail(raw, "AGE_UNPARSEABLE", $"bad number in '{raw}'", materialId, field, raw);
        var unit = m.Groups["unit"].Value.Trim().ToLowerInvariant();
        var years = unit switch
        {
            "a" or "yr" or "yrs" or "year" or "years" or "bp" or "cal" => n,
            "ka" => n * 1000.0,
            _ => null
        };
        if (years is null)
            return Fail(raw, "UNIT_UNKNOWN", $"unsupported age unit '{unit}'", materialId, field, raw);
        var rest = m.Groups["rest"].Value.Trim().ToUpperInvariant();
        if (!string.IsNullOrEmpty(rest) && !rest.Contains("BP"))
            return Fail(raw, "TIME_DATUM_UNKNOWN", $"expected a BP time datum, got '{rest}'", materialId, field, raw);
        return new Measure(raw, years, "a_BP", "CAL-BP", null, null);
    }

    public static Measure ParseYears(string? raw, string? materialId, string field)
    {
        if (string.IsNullOrWhiteSpace(raw))
            return Fail(raw ?? "", "AGE_EMPTY", "value is empty", materialId, field, null);
        var m = TokenRegex().Match(raw.Trim());
        if (!m.Success || !double.TryParse(m!.Groups["num"].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out var n))
            return Fail(raw ?? "", "AGE_UNPARSEABLE", $"cannot parse years '{raw}'", materialId, field, raw);
        var unit = m.Groups["unit"].Value.Trim().ToLowerInvariant();
        var years = unit switch
        {
            "" or "a" or "yr" or "yrs" or "year" or "years" => n,
            "ka" => n * 1000.0,
            _ => null
        };
        return years is null
            ? Fail(raw!, "UNIT_UNKNOWN", $"unsupported unit '{unit}'", materialId, field, raw)
            : new Measure(raw!, years, "a", null, null, null);
    }

    private static Measure Fail(string raw, string code, string detail, string? mid, string field, string? frag)
        => new(raw, null, "", null, code, detail);

    [GeneratedRegex(@"^\s*(?<num>[-+]?[0-9]*\.?[0-9]+(?:[eE][-+]?[0-9]+)?)\s*(?<unit>[a-zA-Z/]*)\s*(?<rest>.*)$")]
    private static partial Regex TokenRegex();
}