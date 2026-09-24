namespace AgeDepth.Domain;

public static class UnitTable
{
    public static readonly IReadOnlyDictionary<string, double> ToMeters =
        new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase)
        {
            ["m"] = 1.0, ["cm"] = 0.01, ["mm"] = 0.001, ["ft"] = 0.3048,
        };
}

/// <summary>
/// A depth value as imported. The raw representation, unit and coordinate datum are
/// always preserved; normalization to meters happens only in the derived layer.
/// Values that cannot be converted stay in place and carry a diagnostic.
/// </summary>
public sealed record DepthQuantity
{
    public required string Raw { get; init; }
    public required string Unit { get; init; }
    public required string Datum { get; init; }
    public double? Meters { get; init; }
    public string? Diagnostic { get; init; }

    public static DepthQuantity Create(string raw, string unit, string datum)
    {
        if (!double.TryParse(raw, System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture, out var value))
            return new DepthQuantity
            {
                Raw = raw, Unit = unit, Datum = datum, Meters = null,
                Diagnostic = $"unparseable-depth:'{raw}'",
            };
        if (!UnitTable.ToMeters.TryGetValue(unit, out var factor))
            return new DepthQuantity
            {
                Raw = raw, Unit = unit, Datum = datum, Meters = null,
                Diagnostic = $"unknown-unit:'{unit}'",
            };
        return new DepthQuantity
        {
            Raw = raw, Unit = unit, Datum = datum,
            Meters = value * factor, Diagnostic = null,
        };
    }
}
