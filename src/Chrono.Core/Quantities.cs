using System.Globalization;
using System.Text.Json.Serialization;

namespace Chrono.Core;

/// <summary>
/// A measurement exactly as it appeared in the source. Normalization to a canonical
/// unit happens only in the derived layer; the original representation is always kept.
/// </summary>
public sealed record RawQuantity
{
    public required string Raw { get; init; }
    public required string Unit { get; init; }
    public string? Datum { get; init; }
    public double? Value { get; init; }
    public bool Convertible { get; init; } = true;
    public string? CanonicalUnit { get; init; }
    public double? NormalizedValue { get; init; }
    public string? Reason { get; init; }

    public static RawQuantity Of(string raw, string unit, string? datum, string kind)
    {
        var parsed = double.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out var v);
        var canonical = UnitCatalog.Canonical(unit, kind);
        if (!parsed || canonical is null)
        {
            return new RawQuantity
            {
                Raw = raw, Unit = unit, Datum = datum, Value = parsed ? v : null,
                Convertible = false, CanonicalUnit = canonical?.Canonical,
                NormalizedValue = null,
                Reason = !parsed ? "UNPARSEABLE_VALUE" : "UNKNOWN_UNIT:" + unit
            };
        }
        var (canon, factor) = canonical!.Value;
        return new RawQuantity
        {
            Raw = raw, Unit = unit, Datum = datum, Value = v,
            Convertible = true, CanonicalUnit = canon, NormalizedValue = v * factor
        };
    }
}

public static class UnitCatalog
{
    // length -> meters
    static readonly Dictionary<string, (string canon, double factor)> Length = new()
    {
        ["m"] = ("m", 1.0), ["meter"] = ("m", 1.0), ["meters"] = ("m", 1.0),
        ["cm"] = ("m", 0.01), ["mm"] = ("m", 0.001)
    };
    // age -> ka (kilo years)
    static readonly Dictionary<string, (string canon, double factor)> Age = new()
    {
        ["ka"] = ("ka", 1.0), ["ka bp"] = ("ka", 1.0), ["cal ka bp"] = ("ka", 1.0),
        ["cal ka"] = ("ka", 1.0), ["kyr"] = ("ka", 1.0), ["kyr bp"] = ("ka", 1.0),
        ["yr"] = ("ka", 0.001), ["yr bp"] = ("ka", 0.001), ["a bp"] = ("ka", 0.001),
        ["cal yr bp"] = ("ka", 0.001)
    };
    static readonly Dictionary<string, (string canon, double factor)> Rate = new()
    {
        ["cm/ka"] = ("cm/ka", 1.0), ["m/ma"] = ("cm/ka", 0.1), ["mm/yr"] = ("cm/ka", 100.0)
    };

    public static (string Canonical, double Factor)? Canonical(string unit, string kind)
    {
        var u = unit.Trim().ToLowerInvariant();
        var table = kind switch { "length" => Length, "age" => Age, "rate" => Rate, _ => null };
        return table is not null && table.TryGetValue(u, out var e) ? e : null;
    }
}
