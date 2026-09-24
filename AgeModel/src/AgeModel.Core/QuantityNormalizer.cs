using System.Globalization;
using System.Text.RegularExpressions;

namespace AgeModel.Core;

public static partial class QuantityNormalizer
{
    public static DomainQuantity NormalizeDepth(DomainQuantity input, string defaultUnit, string datum)
    {
        if (input.NormalizedValue.HasValue) return input;
        if (!TryParse(input.Raw, out var value))
        {
            return input with { Datum = input.Datum ?? datum, Diagnostic = $"Depth value '{input.Raw}' is not numeric." };
        }

        var unit = string.IsNullOrWhiteSpace(input.Unit) ? defaultUnit : input.Unit.Trim();
        return unit.ToLowerInvariant() switch
        {
            "m" or "meter" or "meters" or "m csf-a" or "m cc sf-a" or "m ccsf-a" => input with
            {
                Datum = input.Datum ?? datum,
                NormalizedValue = value,
                NormalizedUnit = "m",
                Diagnostic = null
            },
            "cm" or "centimeter" or "centimeters" => input with
            {
                Datum = input.Datum ?? datum,
                NormalizedValue = value / 100.0,
                NormalizedUnit = "m",
                Diagnostic = null
            },
            _ => input with
            {
                Datum = input.Datum ?? datum,
                Diagnostic = $"Unknown depth unit '{unit}'; original representation retained."
            }
        };
    }

    public static DomainQuantity NormalizeAge(DomainQuantity input)
    {
        if (input.NormalizedValue.HasValue) return input;
        if (!TryParse(input.Raw, out var value))
        {
            return input with { Diagnostic = $"Age value '{input.Raw}' is not numeric." };
        }

        var unit = input.Unit.Trim().ToLowerInvariant();
        return unit switch
        {
            "yr bp" or "years bp" or "a bp" or "cal yr bp" or "cal years bp" or "cal. yr bp" => input with
            {
                NormalizedValue = value,
                NormalizedUnit = "yr BP",
                Diagnostic = null
            },
            "ka bp" or "kyr bp" or "cal ka bp" => input with
            {
                NormalizedValue = value * 1000.0,
                NormalizedUnit = "yr BP",
                Diagnostic = null
            },
            _ => input with { Diagnostic = $"Unknown age unit '{input.Unit}'; original representation retained." }
        };
    }

    public static bool TryParse(string raw, out double value) =>
        double.TryParse(raw.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out value) &&
        double.IsFinite(value);
}
