namespace AgeModel.Core;

public enum DiagSeverity { Error, Warning }

/// <summary>A positioned diagnostic. Never silently replaced by defaults.</summary>
public sealed record Diagnostic(string Code, string Message, string Path, DiagSeverity Severity)
{
    public override string ToString() => $"{Severity}:{Code}@{Path}: {Message}";
}

/// <summary>
/// A domain quantity. The raw representation is immutable and always preserved;
/// normalization happens only in the derived layer. Values that cannot be
/// converted stay in place (Normalized == null) and carry a diagnostic.
/// </summary>
public sealed record Quantity
{
    public required string RawValue { get; init; }
    public required string RawUnit { get; init; }
    /// <summary>Coordinate or time datum, e.g. "mbsf" (depth) or "calBP" (age).</summary>
    public required string Datum { get; init; }
    public double? Normalized { get; init; }
    public string? NormalizedUnit { get; init; }

    public static Quantity Of(double value, string unit, string datum) => new()
    {
        RawValue = value.ToString(System.Globalization.CultureInfo.InvariantCulture),
        RawUnit = unit,
        Datum = datum,
    };
}

/// <summary>Unit registry. Unknown units are not guessed; they fail with diagnostics.</summary>
public static class UnitRegistry
{
    // dimension -> (unit -> factor to canonical unit)
    private static readonly Dictionary<string, Dictionary<string, double>> Table = new()
    {
        ["depth"] = new(StringComparer.OrdinalIgnoreCase)
        {
            ["m"] = 1.0, ["cm"] = 0.01, ["mm"] = 0.001, ["km"] = 1000.0, ["ft"] = 0.3048,
        },
        ["age"] = new(StringComparer.OrdinalIgnoreCase)
        {
            ["ka"] = 1.0, ["Ma"] = 1000.0, ["kyr"] = 1.0, ["yr"] = 0.001, ["a"] = 0.001,
        },
        ["rate"] = new(StringComparer.OrdinalIgnoreCase)
        {
            ["m/ka"] = 1.0, ["cm/yr"] = 10.0, ["m/Myr"] = 0.001,
        },
    };

    public static readonly HashSet<string> KnownDepthDatums = new(StringComparer.OrdinalIgnoreCase)
        { "mbsf", "mcd", "mblf", "m-bsf" };
    public static readonly HashSet<string> KnownAgeDatums = new(StringComparer.OrdinalIgnoreCase)
        { "calBP", "BP", "b2k", "CE" };

    public static string CanonicalUnit(string dimension) => dimension switch
    {
        "depth" => "m",
        "age" => "ka",
        "rate" => "m/ka",
        _ => throw new ArgumentException($"unknown dimension {dimension}"),
    };

    /// <summary>
    /// Normalize a quantity into the canonical unit of its dimension.
    /// On failure the quantity is returned unchanged (raw preserved) plus a diagnostic.
    /// </summary>
    public static (Quantity Quantity, Diagnostic? Diagnostic) Normalize(Quantity q, string dimension, string path)
    {
        if (!Table.TryGetValue(dimension, out var units))
            return (q, new Diagnostic("E-DIM", $"未知量纲 '{dimension}'", path, DiagSeverity.Error));
        if (!double.TryParse(q.RawValue, System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture, out var v))
            return (q, new Diagnostic("E-UNIT-PARSE", $"数值 '{q.RawValue}' 无法解析，原值保留", path, DiagSeverity.Error));
        if (!units.TryGetValue(q.RawUnit, out var factor))
            return (q, new Diagnostic("E-UNIT-UNKNOWN", $"单位 '{q.RawUnit}' 无法转换为 {CanonicalUnit(dimension)}，原值保留", path, DiagSeverity.Error));
        var datumOk = dimension switch
        {
            "depth" => KnownDepthDatums.Contains(q.Datum),
            "age" => KnownAgeDatums.Contains(q.Datum),
            _ => true,
        };
        if (!datumOk)
            return (q, new Diagnostic("E-DATUM", $"坐标/时间基准 '{q.Datum}' 未登记，原值保留", path, DiagSeverity.Error));
        return (q with { Normalized = v * factor, NormalizedUnit = CanonicalUnit(dimension) }, null);
    }
}
