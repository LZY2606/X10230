using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace ChronoScale.Core;

/// <summary>A value that always carries a physical unit or coordinate/time datum.
/// Imported raw representation is preserved; normalization happens only in derived layers.</summary>
public sealed record UnitValue(double? Numeric, string Unit, string? Raw = null)
{
    public bool IsConvertible => Numeric.HasValue;

    public static UnitValue Parse(string? raw, string expectedUnit)
    {
        raw = raw?.Trim();
        if (string.IsNullOrWhiteSpace(raw))
            return new UnitValue(null, expectedUnit, raw);
        if (double.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out var n))
            return new UnitValue(n, expectedUnit, raw);
        return new UnitValue(null, expectedUnit, raw);
    }

    public JsonNode ToNode() => new JsonObject
    {
        ["numeric"] = Numeric?.ToString(CultureInfo.InvariantCulture) ?? null,
        ["unit"] = Unit,
        ["raw"] = Raw
    };

    public string Display => Numeric.HasValue
        ? Numeric.Value.ToString("0.##", CultureInfo.InvariantCulture) + " " + Unit
        : (string.IsNullOrEmpty(Raw) ? "(unconvertible) " + Unit : Raw + " [" + Unit + "]");
}

/// <summary>Depth coordinate reference frame, e.g. centimetres below sea floor.</summary>
public sealed record DepthDatum(string Id, string Name, string DepthUnit, string? ZeroDefinition)
{
    public JsonObject ToNode() => new()
    {
        ["id"] = Id,
        ["name"] = Name,
        ["depthUnit"] = DepthUnit,
        ["zeroDefinition"] = ZeroDefinition
    };
}

/// <summary>Time coordinate reference frame, e.g. years before BP=1950 CE.</summary>
public sealed record TimeDatum(string Id, string Name, string TimeUnit, string? EpochDefinition)
{
    public JsonObject ToNode() => new()
    {
        ["id"] = Id,
        ["name"] = Name,
        ["timeUnit"] = TimeUnit,
        ["epochDefinition"] = EpochDefinition
    };
}
