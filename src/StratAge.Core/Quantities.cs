using System.Globalization;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace StratAge.Core;

/// <summary>
/// A domain measurement that always keeps its original textual representation.
/// Normalisation to the canonical unit happens only on the derived layer;
/// values that cannot be converted stay in place with a diagnostic and are
/// never silently dropped or replaced by a default.
/// </summary>
public sealed record Quant
{
    [JsonPropertyName("raw")] public required string Raw { get; init; }
    [JsonPropertyName("kind")] public required string Kind { get; init; } // length | time
    [JsonPropertyName("canonical")] public double? Canonical { get; init; }
    [JsonPropertyName("canonicalUnit")] public string? CanonicalUnit { get; init; }
    [JsonPropertyName("datum")] public string? Datum { get; init; }
    [JsonPropertyName("converted")] public bool Converted { get; init; }
    [JsonPropertyName("diagnostic")] public string? Diagnostic { get; init; }
    [JsonPropertyName("diagnosticCode")] public string? DiagnosticCode { get; init; }

    public bool Usable => Converted && Canonical.HasValue;

    public static Quant Length(string raw, string? expectedDatum = null) =>
        QuantParser.ParseLength(raw, expectedDatum);

    public static Quant Time(string raw, int refYear = 1950) =>
        QuantParser.ParseTime(raw, refYear);

    public string Display() => Raw;
}

public static partial class QuantParser
{
    private static readonly Regex Num = NumRe();

    [GeneratedRegex(@"^\s*([+-]?[0-9]+(?:[0-9.,]*[0-9])?(?:[eE][+-]?[0-9]+)?)\s*(.*?)\s*$")]
    private static partial Regex NumRe();

    private sealed record LenUnit(double Factor, string Datum);

    // Datum-bearing depth tokens preserve the coordinate reference.
    private static readonly Dictionary<string, LenUnit> LengthUnits = new()
    {
        ["m"] = new(1.0, ""),
        ["cm"] = new(0.01, ""),
        ["mm"] = new(0.001, ""),
        ["mbsf"] = new(1.0, "mbsf"),
        ["mcd"] = new(1.0, "mcd"),
        ["mbll"] = new(1.0, "mbll"),
        ["mblf"] = new(1.0, "mblf"),
        ["cmbsf"] = new(0.01, "mbsf"),
    };

    public static Quant ParseLength(string raw, string? expectedDatum)
    {
        var m = Num.Match(raw);
        if (!m.Success ||
            !double.TryParse(m.Groups[1].Value.Replace(",", ""), NumberStyles.Float,
                CultureInfo.InvariantCulture, out var v))
            return Bad(raw, "length", "UNPARSEABLE_MEASURE",
                $"Value '{raw}' could not be parsed as a length; kept in place, not used in derivation.");

        var unit = m.Groups[2].Value.Trim();
        if (unit.Length == 0)
            return Bad(raw, "length", "MISSING_UNIT",
                $"Depth '{raw}' has no unit or vertical datum; retained raw, excluded from derivation.");

        var key = unit.ToLowerInvariant();
        if (!LengthUnits.TryGetValue(key, out var u))
            return Bad(raw, "length", "UNKNOWN_UNIT",
                $"Length unit/datum '{unit}' is not known; the raw value is retained and no conversion was attempted.");

        var datum = string.IsNullOrEmpty(u.Datum) ? (expectedDatum ?? "mbsf") : u.Datum;
        if (!string.IsNullOrEmpty(expectedDatum) && !string.IsNullOrEmpty(u.Datum) &&
            !string.Equals(u.Datum, expectedDatum, StringComparison.OrdinalIgnoreCase))
            return Bad(raw, "length", "DATUM_MISMATCH",
                $"Depth is referenced to {u.Datum} but the core datum is {expectedDatum}; no datum transform is available, raw value kept.");

        return new Quant
        {
            Raw = raw, Kind = "length", Canonical = v * u.Factor,
            CanonicalUnit = "m", Datum = datum, Converted = true
        };
    }

    private sealed record TimeUnit(double FactorToKa, string Basis);

    private static readonly Dictionary<string, TimeUnit> TimeUnits = new()
    {
        ["ka"] = new(1.0, "BP"),
        ["ka bp"] = new(1.0, "BP"),
        ["cal ka bp"] = new(1.0, "BP"),
        ["kyr bp"] = new(1.0, "BP"),
        ["kyr"] = new(1.0, "BP"),
        ["ma"] = new(1000.0, "BP"),
        ["ma bp"] = new(1000.0, "BP"),
        ["a bp"] = new(0.001, "BP"),
        ["yr bp"] = new(0.001, "BP"),
        ["bp"] = new(0.001, "BP"),
    };

    public static Quant ParseTime(string raw, int refYear)
    {
        var clean = raw.Trim();
        var lower = clean.ToLowerInvariant();
        var m = Num.Match(clean);
        if (!m.Success)
            return Bad(raw, "time", "UNPARSEABLE_MEASURE",
                $"Age '{raw}' could not be parsed; kept in place, not used in derivation.");

        var num = m.Groups[1].Value.Replace(",", "");
        if (!double.TryParse(num, NumberStyles.Float, CultureInfo.InvariantCulture, out var v))
            return Bad(raw, "time", "UNPARSEABLE_MEASURE",
                $"Age '{raw}' could not be parsed; kept in place, not used in derivation.");

        var unit = m.Groups[2].Value.Trim();
        var key = unit.ToLowerInvariant();
        if (key.Contains("ce") || key.Contains("ad") || key.Contains("bce") || key.Contains("bc"))
            return Bad(raw, "time", "TIME_BASIS_UNSUPPORTED",
                $"Age '{raw}' is on a calendar (CE/BCE) basis; conversion to ka BP needs a declared reference year and is not configured. Raw value kept.");

        if (key.Length == 0)
            return Bad(raw, "time", "MISSING_UNIT",
                $"Age '{raw}' has no unit/time basis; retained raw, excluded from derivation.");

        if (!TimeUnits.TryGetValue(key, out var tu))
            return Bad(raw, "time", "UNKNOWN_UNIT",
                $"Time unit/basis '{unit}' is not known; the raw value is retained and no conversion was attempted.");

        var ka = v * tu.FactorToKa;
        return new Quant
        {
            Raw = raw, Kind = "time", Canonical = ka,
            CanonicalUnit = "ka", Datum = $"{tu.Basis} (reference year {refYear})", Converted = true
        };
    }

    private static Quant Bad(string raw, string kind, string code, string message) => new()
    {
        Raw = raw, Kind = kind, Canonical = null, CanonicalUnit = null,
        Converted = false, Diagnostic = message, DiagnosticCode = code
    };
}
