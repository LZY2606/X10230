using System.Globalization;
using System.Text.RegularExpressions;

namespace ChronoCore;

/// <summary>Depth datum plus value in canonical metres below sea floor.</summary>
public sealed record DepthPoint
{
    public required string Raw { get; init; }
    public required string Datum { get; init; }            // e.g. "mbsf@seafloor"
    public required string Unit { get; init; }            // raw unit token
    public double? Metres { get; init; }                  // canonical, null = unconvertible
    public string Diagnostics { get; init; } = "";

    public bool IsValid => Metres.HasValue;

    public static DepthPoint Parse(object raw, string datum)
    {
        string text = raw?.ToString()?.Trim() ?? "";
        var match = Regex.Match(text, @"^([+-]?\d+(?:\.\d+)?)\s*([A-Za-z]+)$");
        if (!match.Success)
            return new DepthPoint { Raw = text, Datum = datum, Unit = "", Metres = null,
                Diagnostics = "UNCONVERTIBLE_DEPTH: numeric value with unit expected" };
        double value = double.Parse(match.Groups[1].Value, CultureInfo.InvariantCulture);
        string unit = match.Groups[2].Value.ToLowerInvariant();
        double factor = unit switch
        {
            "m" or "mbsf" or "mcd" => 1.0,
            "cm" => 0.01,
            "mm" => 0.001,
            "ft" => 0.3048,
            _ => double.NaN
        };
        if (double.IsNaN(factor))
            return new DepthPoint { Raw = text, Datum = datum, Unit = unit, Metres = null,
                Diagnostics = $"UNCONVERTIBLE_UNIT:{unit}" };
        if (value < 0)
            return new DepthPoint { Raw = text, Datum = datum, Unit = unit, Metres = null,
                Diagnostics = "SEMANTIC_ERROR: negative depth" };
        return new DepthPoint
        {
            Raw = text, Datum = datum, Unit = unit,
            Metres = Math.Round(value * factor, 6)
        };
    }
}

/// <summary>Age is measured in years BP (canonical). 14C ages carry a reservoir offset.</summary>
public sealed record AgeQuantity
{
    public required string Raw { get; init; }
    public required string Unit { get; init; }
    public double? YearsBp { get; init; }
    public string Diagnostics { get; init; } = "";

    public bool IsValid => YearsBp.HasValue;

    public static AgeQuantity Parse(object raw)
    {
        string text = raw?.ToString()?.Trim() ?? "";
        var match = Regex.Match(text, @"^([+-]?\d+(?:\.\d+)?)\s*([A-Za-z]+)$");
        if (!match.Success)
            return new AgeQuantity { Raw = text, Unit = "", YearsBp = null,
                Diagnostics = "UNCONVERTIBLE_AGE: numeric value with unit expected" };
        double value = double.Parse(match.Groups[1].Value, CultureInfo.InvariantCulture);
        string unit = match.Groups[2].Value.ToLowerInvariant();
        double factor = unit switch
        {
            "a" or "yr" or "yrs" or "y" or "bp" => 1.0,
            "ka" => 1000.0,
            "ma" => 1_000_000.0,
            _ => double.NaN
        };
        if (double.IsNaN(factor))
            return new AgeQuantity { Raw = text, Unit = unit, YearsBp = null,
                Diagnostics = $"UNCONVERTIBLE_AGE_UNIT:{unit}" };
        return new AgeQuantity { Raw = text, Unit = unit, YearsBp = Math.Round(value * factor, 3) };
    }
}
