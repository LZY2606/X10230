namespace Chronostrat.Core;

/// <summary>A physical quantity that always carries its unit or coordinate basis.</summary>
public sealed record Quantity(double Value, string Unit)
{
    public override string ToString() => $"{Value} {Unit}";
}

/// <summary>
/// Unit normalization. Raw representations are preserved on import; only the
/// derived layer normalizes. Values that cannot be converted stay in place and
/// get a diagnostic — they are never silently dropped for plotting.
/// </summary>
public static class UnitRegistry
{
    private static readonly Dictionary<string, (string Canonical, double Factor)> DepthUnits = new(StringComparer.OrdinalIgnoreCase)
    {
        ["m"] = ("m", 1.0),
        ["cm"] = ("m", 0.01),
        ["mm"] = ("m", 0.001),
        ["km"] = ("m", 1000.0),
    };

    private static readonly Dictionary<string, (string Canonical, double Factor)> AgeUnits = new(StringComparer.OrdinalIgnoreCase)
    {
        ["ka"] = ("ka", 1.0),
        ["Ma"] = ("ka", 1000.0),
        ["yr"] = ("ka", 0.001),
        ["kyr"] = ("ka", 1.0),
        ["a"] = ("ka", 0.001),
    };

    private static readonly Dictionary<string, (string Canonical, double Factor)> RateUnits = new(StringComparer.OrdinalIgnoreCase)
    {
        ["cm/ka"] = ("cm/ka", 1.0),
        ["m/ka"] = ("cm/ka", 100.0),
        ["mm/yr"] = ("cm/ka", 100.0),
    };

    public static bool TryNormalizeDepth(Quantity q, out Quantity normalized)
        => Try(q, DepthUnits, out normalized);

    public static bool TryNormalizeAge(Quantity q, out Quantity normalized)
        => Try(q, AgeUnits, out normalized);

    public static bool TryNormalizeRate(Quantity q, out Quantity normalized)
        => Try(q, RateUnits, out normalized);

    private static bool Try(Quantity q, Dictionary<string, (string Canonical, double Factor)> table, out Quantity normalized)
    {
        if (table.TryGetValue(q.Unit, out var entry))
        {
            normalized = new Quantity(q.Value * entry.Factor, entry.Canonical);
            return true;
        }
        normalized = q;
        return false;
    }
}

/// <summary>Coordinate basis for depths, e.g. meters below seafloor.</summary>
public sealed record DepthDatum(
    string Id,
    string Name,
    string ReferenceLevel,   // e.g. "seafloor", "kelly-bushing"
    string Unit,             // canonical depth unit, e.g. "m"
    string Orientation       // "down-positive" | "up-positive"
);

/// <summary>A diagnostic always carries a position so failures are locatable.</summary>
public sealed record Diagnostic(
    string Code,
    string Message,
    string Position,          // e.g. "anchor:anch-7@depth", "segment:seg-2", "archive:byte=412"
    string Severity           // "error" | "warning"
);
