using System.Globalization;
using System.Text.Json.Nodes;

namespace AgeDepth.Core;

/// <summary>
/// Derived normalization layer. Raw values are never rewritten; unconvertible
/// values stay in place with a diagnostic instead of being silently dropped.
/// </summary>
public static class Normalize
{
    public const string CanonicalDatum = "mbsf";
    public const string CanonicalAgeUnit = "yrBP";

    public static bool TryDepthToMeters(double value, string? unit, out double meters)
    {
        meters = value;
        switch ((unit ?? "m").Trim().ToLowerInvariant())
        {
            case "m": case "meter": case "meters": return true;
            case "cm": meters = value / 100.0; return true;
            case "mm": meters = value / 1000.0; return true;
            case "ft": case "feet": meters = value * 0.3048; return true;
            default: return false;
        }
    }

    public static bool TryAgeToYrBp(double value, string? unit, out double yrBp)
    {
        yrBp = value;
        switch ((unit ?? "yrBP").Trim().ToLowerInvariant().Replace(" ", ""))
        {
            case "yrbp": case "calyrbp": case "yr": case "yrs": case "years": case "bp": return true;
            case "kabp": case "ka": case "kyr": case "kyrbp": yrBp = value * 1000.0; return true;
            case "mabp": case "ma": yrBp = value * 1_000_000.0; return true;
            default: return false;
        }
    }

    public static bool TryParseNumber(JsonNode? node, out double value)
    {
        value = 0;
        switch (node)
        {
            case null: return false;
            case JsonValue v when v.TryGetValue<double>(out var d): value = d; return true;
            case JsonValue v when v.TryGetValue<string>(out var s):
                return double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out value);
            default: return false;
        }
    }

    /// <summary>Reads a {value, unit, datum} quantity; keeps the raw string form.</summary>
    public static JsonObject? Quantity(JsonObject raw, string field, string entityId, int? position,
        List<Diagnostic> diags, out double meters)
    {
        meters = 0;
        if (raw[field] is not JsonObject q)
        {
            diags.Add(new Diagnostic("QUANTITY_MISSING", "error", entityId, field, position,
                $"quantity '{field}' is missing or not an object", raw[field]?.ToJsonString()));
            return null;
        }
        var rawValue = q["value"];
        if (!TryParseNumber(rawValue, out var parsed))
        {
            diags.Add(new Diagnostic("QUANTITY_UNPARSEABLE", "error", entityId, field, position,
                $"value of '{field}' cannot be parsed as a number; left in raw form", rawValue?.ToJsonString()));
            return null;
        }
        var unit = q["unit"]?.GetValue<string>();
        if (!TryDepthToMeters(parsed, unit, out meters))
        {
            diags.Add(new Diagnostic("UNIT_UNCONVERTIBLE", "error", entityId, field, position,
                $"unit '{unit}' of '{field}' has no conversion to m; value kept raw, excluded from compute",
                rawValue?.ToJsonString()));
            return null;
        }
        return new JsonObject
        {
            ["m"] = meters,
            ["datum"] = q["datum"]?.GetValue<string>() ?? CanonicalDatum,
            ["rawValue"] = rawValue?.DeepClone(),
            ["rawUnit"] = unit
        };
    }

    public static JsonObject? AgeQuantity(JsonObject raw, string field, string entityId, int? position,
        List<Diagnostic> diags, out double yrBp)
    {
        yrBp = 0;
        if (raw[field] is not JsonObject q)
        {
            diags.Add(new Diagnostic("QUANTITY_MISSING", "error", entityId, field, position,
                $"quantity '{field}' is missing or not an object", raw[field]?.ToJsonString()));
            return null;
        }
        var rawValue = q["value"];
        if (!TryParseNumber(rawValue, out var parsed))
        {
            diags.Add(new Diagnostic("QUANTITY_UNPARSEABLE", "error", entityId, field, position,
                $"value of '{field}' cannot be parsed as a number; left in raw form", rawValue?.ToJsonString()));
            return null;
        }
        var unit = q["unit"]?.GetValue<string>();
        if (!TryAgeToYrBp(parsed, unit, out yrBp))
        {
            diags.Add(new Diagnostic("UNIT_UNCONVERTIBLE", "error", entityId, field, position,
                $"unit '{unit}' of '{field}' has no conversion to yr BP; value kept raw, excluded from compute",
                rawValue?.ToJsonString()));
            return null;
        }
        return new JsonObject
        {
            ["yrBP"] = yrBp,
            ["rawValue"] = rawValue?.DeepClone(),
            ["rawUnit"] = unit
        };
    }

    /// <summary>Normalize one raw material into its derived view (or null + diagnostics).</summary>
    public static JsonObject? Material(MaterialKind kind, JsonObject raw, string entityId, int? position,
        IReadOnlyDictionary<string, double> datumOffsets, List<Diagnostic> diags)
    {
        return kind switch
        {
            MaterialKind.AgeAnchor => Anchor(raw, entityId, position, datumOffsets, diags),
            MaterialKind.CoreSection => Section(raw, entityId, position, datumOffsets, diags),
            MaterialKind.Boundary => Boundary(raw, entityId, position, datumOffsets, diags),
            MaterialKind.EventLayer => EventLayer(raw, entityId, position, datumOffsets, diags),
            MaterialKind.DepthDatum => Datum(raw, entityId, position, diags),
            _ => null
        };
    }

    private static double ApplyDatum(double depthM, string? datum, IReadOnlyDictionary<string, double> offsets,
        string entityId, string field, int? position, List<Diagnostic> diags)
    {
        var d = datum ?? CanonicalDatum;
        if (d == CanonicalDatum) return depthM;
        if (offsets.TryGetValue(d, out var off)) return depthM + off;
        diags.Add(new Diagnostic("DATUM_UNKNOWN", "error", entityId, field, position,
            $"depth datum '{d}' is not registered; value kept raw, excluded from compute", d));
        return double.NaN;
    }

    private static JsonObject? Anchor(JsonObject raw, string id, int? pos,
        IReadOnlyDictionary<string, double> datumOffsets, List<Diagnostic> diags)
    {
        var depth = Quantity(raw, "depth", id, pos, diags, out var depthM);
        if (depth is null) return null;
        var datum = depth["datum"]!.GetValue<string>();
        var shifted = ApplyDatum(depthM, datum, datumOffsets, id, "depth", pos, diags);
        if (double.IsNaN(shifted)) return null;

        if (raw["distribution"] is not JsonObject dist || dist["samples"] is not JsonArray samples || samples.Count == 0)
        {
            diags.Add(new Diagnostic("DISTRIBUTION_MISSING", "error", id, "distribution", pos,
                "anchor has no usable age distribution samples", raw["distribution"]?.ToJsonString()));
            return null;
        }
        var ageUnit = dist["unit"]?.GetValue<string>();
        var vals = new List<double>();
        var i = 0;
        foreach (var s in samples)
        {
            if (!TryParseNumber(s, out var v) || !TryAgeToYrBp(v, ageUnit, out var conv))
            {
                diags.Add(new Diagnostic("UNIT_UNCONVERTIBLE", "error", id, $"distribution.samples[{i}]", pos,
                    $"sample {i} cannot be converted to yr BP; sample kept raw in place", s?.ToJsonString()));
                i++;
                continue;
            }
            vals.Add(conv);
            i++;
        }
        if (vals.Count == 0)
        {
            diags.Add(new Diagnostic("DISTRIBUTION_EMPTY", "error", id, "distribution", pos,
                "no convertible samples remain in the distribution", null));
            return null;
        }
        vals.Sort();

        double? reservoirYr = null;
        if (raw["reservoirOffset"] is JsonObject ro &&
            AgeQuantity(raw, "reservoirOffset", id, pos, diags, out var offYr) is not null)
            reservoirYr = offYr;
        var applied = raw["reservoirApplied"]?.GetValue<bool>() ?? false;
        var effective = vals.Select(v => applied || reservoirYr is null ? v : v - reservoirYr.Value).ToArray();
        Array.Sort(effective);

        return new JsonObject
        {
            ["depthM"] = shifted,
            ["datum"] = CanonicalDatum,
            ["samplesYrBp"] = new JsonArray(vals.Select(v => (JsonNode)JsonValue.Create(v)!).ToArray()),
            ["effectiveYrBp"] = new JsonArray(effective.Select(v => (JsonNode)JsonValue.Create(v)!).ToArray()),
            ["reservoirOffsetYr"] = reservoirYr,
            ["reservoirApplied"] = applied,
            ["method"] = raw["method"]?.GetValue<string>() ?? "unknown",
            ["lab"] = raw["lab"]?.GetValue<string>()
        };
    }

    private static JsonObject? Section(JsonObject raw, string id, int? pos,
        IReadOnlyDictionary<string, double> datumOffsets, List<Diagnostic> diags)
    {
        var top = Quantity(raw, "top", id, pos, diags, out var topM);
        var bottom = Quantity(raw, "bottom", id, pos, diags, out var bottomM);
        if (top is null || bottom is null) return null;
        var t = ApplyDatum(topM, top["datum"]!.GetValue<string>(), datumOffsets, id, "top", pos, diags);
        var b = ApplyDatum(bottomM, bottom["datum"]!.GetValue<string>(), datumOffsets, id, "bottom", pos, diags);
        if (double.IsNaN(t) || double.IsNaN(b)) return null;
        if (b <= t)
            diags.Add(new Diagnostic("SECTION_INVERTED", "error", id, "bottom", pos,
                "section bottom is not below its top; section retained but excluded from segmentation", null));
        return new JsonObject
        {
            ["core"] = raw["core"]?.GetValue<string>() ?? "?",
            ["topM"] = t,
            ["bottomM"] = b,
            ["datum"] = CanonicalDatum
        };
    }

    private static JsonObject? Boundary(JsonObject raw, string id, int? pos,
        IReadOnlyDictionary<string, double> datumOffsets, List<Diagnostic> diags)
    {
        var depth = Quantity(raw, "depth", id, pos, diags, out var depthM);
        if (depth is null) return null;
        var d = ApplyDatum(depthM, depth["datum"]!.GetValue<string>(), datumOffsets, id, "depth", pos, diags);
        if (double.IsNaN(d)) return null;
        var bt = raw["boundaryType"]?.GetValue<string>() ?? "gap";
        if (bt is not ("gap" or "unconformity" or "fault"))
            diags.Add(new Diagnostic("BOUNDARY_TYPE_UNKNOWN", "warning", id, "boundaryType", pos,
                $"boundary type '{bt}' is not one of gap|unconformity|fault; treated as gap", bt));
        return new JsonObject
        {
            ["depthM"] = d,
            ["boundaryType"] = bt is "unconformity" or "fault" ? bt : "gap"
        };
    }

    private static JsonObject? EventLayer(JsonObject raw, string id, int? pos,
        IReadOnlyDictionary<string, double> datumOffsets, List<Diagnostic> diags)
    {
        var depth = Quantity(raw, "depth", id, pos, diags, out var depthM);
        if (depth is null) return null;
        var d = ApplyDatum(depthM, depth["datum"]!.GetValue<string>(), datumOffsets, id, "depth", pos, diags);
        if (double.IsNaN(d)) return null;
        var norm = new JsonObject
        {
            ["depthM"] = d,
            ["eventType"] = raw["eventType"]?.GetValue<string>() ?? "event",
            ["correlatesTo"] = raw["correlatesTo"]?.GetValue<string>()
        };
        if (raw["age"] is JsonObject &&
            AgeQuantity(raw, "age", id, pos, diags, out var ageYr) is not null)
            norm["ageYrBp"] = ageYr;
        return norm;
    }

    private static JsonObject? Datum(JsonObject raw, string id, int? pos, List<Diagnostic> diags)
    {
        var name = raw["name"]?.GetValue<string>();
        if (string.IsNullOrWhiteSpace(name))
        {
            diags.Add(new Diagnostic("DATUM_NAME_MISSING", "error", id, "name", pos, "datum has no name", null));
            return null;
        }
        double offset = 0;
        if (raw["offsetToMbsf"] is JsonObject &&
            Quantity(raw, "offsetToMbsf", id, pos, diags, out var off) is not null)
            offset = off;
        return new JsonObject
        {
            ["name"] = name,
            ["offsetToMbsfM"] = offset,
            ["orientation"] = raw["orientation"]?.GetValue<string>() ?? "down-positive"
        };
    }
}
