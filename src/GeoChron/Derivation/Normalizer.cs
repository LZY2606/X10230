using System.Globalization;
using System.Text.Json.Nodes;
using GeoChron.Domain;

namespace GeoChron.Derivation;

/// <summary>
/// Turns immutable source material into resolved depth/age objects.
/// Unconvertible values stay in place with diagnostics instead of being dropped.
/// </summary>
public sealed class Normalizer
{
    public List<Diag> Diags { get; } = new();

    public static double? MetersFrom(double v, string? unit) => unit?.Trim().ToLowerInvariant() switch
    {
        "m" or "metre" or "metres" or "meter" or "meters" => v,
        "cm" => v / 100.0,
        "mm" => v / 1000.0,
        _ => null
    };

    public DepthPoint ParseDepth(JsonObject raw, string loc)
    {
        var rawText = raw["depth"]?.ToJsonString() ?? "";
        var datum = raw["datum"]?.GetValue<string>();
        if (string.IsNullOrWhiteSpace(datum))
        {
            Diags.Add(Diag.Error("DATUM_MISSING", loc, $"depth {rawText} has no datum reference"));
            return new DepthPoint(rawText, double.NaN, datum ?? "", false, null);
        }
        var depthNode = raw["depth"];
        double val;
        string? unit;
        if (depthNode is JsonObject do2)
        {
            if (do2["value"] is null || !TryDouble(do2["value"], out val))
            {
                Diags.Add(Diag.Error("DEPTH_UNPARSEABLE", loc, $"depth value '{rawText}' is not numeric"));
                return new DepthPoint(rawText, double.NaN, datum, false, null);
            }
            unit = do2["unit"]?.GetValue<string>();
        }
        else if (depthNode is JsonValue dv && dv.TryGetValue<double>(out var dvv))
        {
            val = dvv; unit = "m";
        }
        else if (depthNode is JsonValue ds && ds.TryGetValue<string>(out var dss))
        {
            var parsed = ParseNumberUnit(dss, loc);
            if (parsed is null) return new DepthPoint(rawText, double.NaN, datum, false, null);
            (val, unit) = parsed.Value;
        }
        else
        {
            Diags.Add(Diag.Error("DEPTH_UNPARSEABLE", loc, $"depth '{rawText}' is not numeric"));
            return new DepthPoint(rawText, double.NaN, datum, false, null);
        }
        var m = MetersFrom(val, unit);
        if (m is null)
        {
            Diags.Add(Diag.Error("DEPTH_UNIT_UNKNOWN", loc, $"depth {val} {unit}: unit cannot be converted to m"));
            return new DepthPoint(rawText, double.NaN, datum, false, unit);
        }
        return new DepthPoint(rawText, m.Value, datum, true, unit);
    }

    (double, string)? ParseNumberUnit(string s, string loc)
    {
        s = s.Trim();
        var ui = -1;
        for (var k = 0; k < s.Length; k++)
            if (char.IsLetter(s[k])) { ui = k; break; }
        var num = ui < 0 ? s : s[..ui].TrimEnd();
        var unit = ui < 0 ? "m" : s[ui..].Trim().ToLowerInvariant();
        if (!double.TryParse(num, NumberStyles.Float, CultureInfo.InvariantCulture, out var v))
        {
            Diags.Add(Diag.Error("DEPTH_UNPARSEABLE", loc, $"depth value '{s}' is not numeric"));
            return null;
        }
        return (v, unit);
    }

    static bool TryDouble(JsonNode? n, out double v)
    {
        v = 0;
        if (n is JsonValue jv && jv.TryGetValue<double>(out var d)) { v = d; return true; }
        if (n is JsonValue js && js.TryGetValue<string>(out var s)
            && double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out var d2)) { v = d2; return true; }
        return false;
    }
}
