using System.Globalization;
using System.Text.Json.Serialization;

namespace AgeDepth.Domain;

/// <summary>
/// A physical quantity imported verbatim. The raw textual representation is always
/// retained; the derived layer performs unit normalisation and attaches a diagnostic
/// when the value cannot be converted. Values are never silently defaulted.
/// </summary>
public sealed record Quantity
{
    public string Raw { get; init; } = "";
    public double? Value { get; init; }
    public string Unit { get; init; } = "";
    public string? Diagnostic { get; init; }

    [JsonIgnore]
    public bool IsUsable => Value.HasValue && Diagnostic is null;

    public static Quantity Parse(string raw, string kind)
    {
        var text = raw.Trim();
        if (text.Length == 0)
            return new Quantity { Raw = raw, Diagnostic = $"empty {kind}" };

        var unit = "";
        var numberPart = text;
        foreach (var (u, token) in UnitTokens(kind))
        {
            if (text.EndsWith(token, StringComparison.OrdinalIgnoreCase))
            {
                unit = u;
                numberPart = text[..^token.Length].TrimEnd();
                break;
            }
        }

        if (!double.TryParse(numberPart, NumberStyles.Float, CultureInfo.InvariantCulture, out var value)
            || double.IsNaN(value) || double.IsInfinity(value))
        {
            return new Quantity { Raw = raw, Unit = unit, Diagnostic = $"unparseable {kind}: '{raw}'" };
        }

        if (unit.Length == 0)
            return new Quantity { Raw = raw, Value = value, Diagnostic = $"missing unit for {kind}: '{raw}'" };

        var (normalised, targetUnit, factor) = Normalise(value, unit, kind);
        return new Quantity
        {
            Raw = raw,
            Value = normalised,
            Unit = targetUnit,
            Diagnostic = factor is null
                ? $"cannot convert unit '{unit}' to {kind} normalisation unit ({targetUnit}); raw value retained"
                : null
        };
    }

    private static IEnumerable<(string Unit, string Token)> UnitTokens(string kind) => kind switch
    {
        "depth" => new[]
        {
            ("mbsf", "mbsf"), ("mcd", "mcd"), ("m", "m"), ("cm", "cm"), ("mm", "mm")
        }.Select(t => (t, t)),
        "age" => new[]
        {
            ("ka", "ka"), ("cal ka BP", "cal ka bp"), ("ka BP", "ka bp"),
            ("a", "a"), ("yr BP", "yr bp"), ("yr", "yr")
        }.Select(t => (t.Item1, t.Item2)),
        _ => Array.Empty<(string, string)>()
    };

    private static (double Value, string Target, double? Factor) Normalise(double value, string unit, string kind)
    {
        if (kind == "depth")
        {
            return unit switch
            {
                "m" or "mbsf" or "mcd" => (value, unit, 1.0),
                "cm" => (value / 100.0, "m", 0.01),
                "mm" => (value / 1000.0, "m", 0.001),
                _ => (value, "m", null)
            };
        }

        return unit switch
        {
            "ka" or "cal ka BP" or "ka BP" => (value, "ka", 1.0),
            "a" or "yr BP" or "yr" => (value / 1000.0, "ka", 0.001),
            _ => (value, "ka", null)
        };
    }
}
