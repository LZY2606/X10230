using System.Globalization;
using System.Text.Json.Serialization;

namespace CoreChrono.Domain;

/// <summary>A physical value carrying its original textual representation and unit/datum.
/// The raw text is never lost on import; normalization happens only on a derived layer.
/// A value that cannot be converted stays in place (Normalized==null) and carries a diagnostic.</summary>
public sealed record Measure
{
    public required string Raw { get; init; }
    public required string Unit { get; init; }
    public string? Datum { get; init; }
    public double? Normalized { get; init; }
    public string? NormalizedUnit { get; init; }
    public string? ConversionDiagnostic { get; init; }

    public bool IsConvertible => Normalized.HasValue && ConversionDiagnostic is null;

    public static Measure Depth(double meters, string raw, string unit = "m", string datum = "mbsf")
        => new() { Raw = raw, Unit = unit, Datum = datum, Normalized = meters, NormalizedUnit = "m" };

    public static Measure Age(double kaBp, string raw, string unit = "ka BP")
        => new() { Raw = raw, Unit = unit, Datum = "BP1950", Normalized = kaBp, NormalizedUnit = "ka" };

    public static Measure Unconvertible(string raw, string unit, string why)
        => new() { Raw = raw, Unit = unit, Normalized = null, NormalizedUnit = null, ConversionDiagnostic = why };
}

/// <summary>Depth reference frame. All depths are normalized to metres below the declared datum.</summary>
public sealed record DepthDatum
{
    public required string Id { get; init; }
    public required string Name { get; init; }
    public required string VerticalDirection { get; init; } // "down-positive"
    public required string NormalizedUnit { get; init; }     // "m"
    public double? ReferenceOffsetM { get; init; }
}

public static class UnitTables
{
    public static readonly IReadOnlyDictionary<string, double> Length =
        new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase)
        {
            ["m"] = 1.0, ["meter"] = 1.0, ["meters"] = 1.0, ["metre"] = 1.0, ["metres"] = 1.0,
            ["cm"] = 0.01, ["mm"] = 0.001,
            ["mbsf"] = 1.0,
            ["ft"] = 0.3048, ["feet"] = 0.3048, ["in"] = 0.0254,
        };

    public static readonly IReadOnlyDictionary<string, double> Time =
        new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase)
        {
            ["ka bp"] = 1.0, ["ka"] = 1.0, ["kyr"] = 1.0, ["kyr bp"] = 1.0, ["ka b.p."] = 1.0,
            ["a bp"] = 0.001, ["yr bp"] = 0.001, ["years bp"] = 0.001, ["y bp"] = 0.001,
            ["ma bp"] = 1000.0, ["ma"] = 1000.0, ["mya"] = 1000.0,
        };

    public static double? TryConvertLength(string raw, string unit, out string? diag)
    {
        diag = null;
        if (!double.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out var v))
        { diag = $"depth '{raw}' is not numeric"; return null; }
        if (!Length.TryGetValue(unit.Trim(), out var f))
        { diag = $"length unit '{unit}' has no conversion to m"; return null; }
        return v * f;
    }

    public static double? TryConvertTime(string raw, string unit, out string? diag)
    {
        diag = null;
        if (!double.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out var v))
        { diag = $"age '{raw}' is not numeric"; return null; }
        if (!Time.TryGetValue(unit.Trim(), out var f))
        { diag = $"time unit '{unit}' has no conversion to ka"; return null; }
        return v * f;
    }
}
