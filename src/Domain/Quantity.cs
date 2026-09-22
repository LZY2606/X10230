namespace ChronoCore.Domain;

/// <summary>A measured quantity that always carries its unit and (for depths) its datum.</summary>
public sealed record Quantity(double Value, string Unit, string? Datum = null)
{
    public override string ToString() => Datum is null ? $"{Value} {Unit}" : $"{Value} {Unit} [{Datum}]";
}

/// <summary>Structured, machine-readable diagnostic attached to a location in the input.</summary>
public sealed record Diagnostic(
    string Code,
    string Severity,   // "error" | "warning"
    string Location,   // e.g. "materials.anchors[2].depth"
    string Message,
    string? MaterialId = null);

public static class Units
{
    public const string CanonDepthUnit = "m";
    public const string CanonAgeUnit = "yrBP";

    /// <summary>Try to convert a depth to canonical metres. Datum changes are never implied.</summary>
    public static bool TryConvertDepth(Quantity q, string targetDatum, out double metres, out string? reason)
    {
        metres = 0; reason = null;
        var datum = q.Datum ?? targetDatum;
        if (!string.Equals(datum, targetDatum, StringComparison.OrdinalIgnoreCase))
        {
            reason = $"depth datum '{datum}' cannot be reconciled with profile datum '{targetDatum}' without an explicit tie";
            return false;
        }
        switch (q.Unit)
        {
            case "m": metres = q.Value; return true;
            case "cm": metres = q.Value / 100.0; return true;
            case "mm": metres = q.Value / 1000.0; return true;
            default: reason = $"unknown depth unit '{q.Unit}'"; return false;
        }
    }

    /// <summary>Try to convert an age to canonical yrBP.</summary>
    public static bool TryConvertAge(Quantity q, out double yrBp, out string? reason)
    {
        yrBp = 0; reason = null;
        switch (q.Unit)
        {
            case "yrBP": case "cal yr BP": yrBp = q.Value; return true;
            case "ka": yrBp = q.Value * 1000.0; return true;
            case "Ma": yrBp = q.Value * 1_000_000.0; return true;
            default: reason = $"unknown age unit '{q.Unit}'"; return false;
        }
    }
}
