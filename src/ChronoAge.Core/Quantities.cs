using System.Globalization;

namespace ChronoAge.Core;

public enum Dimension { Depth, Age, Offset, Rate }

/// <summary>
/// A measured quantity. The original textual representation and unit/datum token are
/// preserved verbatim (immutable layer). Canonical SI-like normalization happens only on
/// the derived layer. When conversion is impossible the value stays in place with a
/// diagnostic and <see cref="Canonical"/> is null — it is never dropped or zeroed.
/// </summary>
public sealed record Measure(
    Dimension Dimension,
    string Raw,
    string? Unit,
    string? Datum,
    double? Canonical,
    IReadOnlyList<Diagnostic> Diagnostics)
{
    /// <summary>Canonical units: depth m, age yr BP, offset yr, rate cm/yr.</summary>
    public string CanonicalUnit => Dimension switch
    {
        Dimension.Depth => "m",
        Dimension.Age => "yr_BP",
        Dimension.Offset => "yr",
        Dimension.Rate => "cm/yr",
        _ => "?"
    };

    public bool IsConvertible => Canonical.HasValue && Diagnostics.All(d => !d.IsError);

    public static Measure Parse(Dimension dim, string? rawToken, string? unit, string? datum, string? objectId, string? field)
    {
        var diags = new List<Diagnostic>();
        var raw = rawToken?.Trim() ?? "";
        if (raw.Length == 0)
        {
            diags.Add(Diag.Error("QTY_EMPTY", "Quantity has no original representation; empty values are not accepted as success.", objectId, field));
            return new Measure(dim, raw, unit, datum, null, diags);
        }

        // Split an inline trailing unit such as "12.40 mbsf" while preserving the token.
        var numericPart = raw;
        var effectiveUnit = unit;
        var sp = raw.IndexOf(' ');
        if (sp > 0)
        {
            numericPart = raw[..sp].Trim();
            effectiveUnit ??= raw[(sp + 1)..].Trim();
        }

        if (!double.TryParse(numericPart, NumberStyles.Float, CultureInfo.InvariantCulture, out var value))
        {
            diags.Add(Diag.Error("QTY_NOT_NUMERIC", $"Value '{raw}' cannot be parsed as a number and is kept at its original position.", objectId, field, raw));
            return new Measure(dim, raw, effectiveUnit, datum, null, diags);
        }

        var (factor, needsCalibration) = UnitRegistry.FactorTo(dim, effectiveUnit);
        if (needsCalibration)
        {
            diags.Add(Diag.Warn("QTY_NEEDS_CALIBRATION",
                $"Unit '{effectiveUnit}' is a radiocarbon scale; calendar normalization requires a calibration curve and reservoir correction. Raw value retained.",
                objectId, field, raw));
            return new Measure(dim, raw, effectiveUnit, datum, null, diags);
        }
        if (factor is null)
        {
            diags.Add(Diag.Error("QTY_UNKNOWN_UNIT",
                $"Unit '{effectiveUnit}' is not in the registry for dimension {dim}; the value is retained without normalization.",
                objectId, field, raw));
            return new Measure(dim, raw, effectiveUnit, datum, null, diags);
        }

        if (dim == Dimension.Depth && datum is not null && !DatumRegistry.IsKnown(datum))
        {
            diags.Add(Diag.Error("QTY_UNKNOWN_DATUM",
                $"Depth datum '{datum}' is not defined by the project depth-datum table; cannot normalize safely. Raw value retained.",
                objectId, field, raw));
            return new Measure(dim, raw, effectiveUnit, datum, null, diags);
        }

        return new Measure(dim, raw, effectiveUnit, datum, value * factor.Value, diags);
    }
}

/// <summary>Conversion factors to canonical units. Null factor = unknown; calibration flag = radiocarbon scale.</summary>
public static class UnitRegistry
{
    private static readonly Dictionary<(Dimension, string), double> Factors = new()
    {
        [(Dimension.Depth, "m")] = 1.0,
        [(Dimension.Depth, "mbsf")] = 1.0,
        [(Dimension.Depth, "msf")] = 1.0,
        [(Dimension.Depth, "mcd")] = 1.0,
        [(Dimension.Depth, "cm")] = 0.01,
        [(Dimension.Depth, "ft")] = 0.3048,
        [(Dimension.Depth, "in")] = 0.0254,
        [(Dimension.Age, "yr_BP")] = 1.0,
        [(Dimension.Age, "cal_yr_BP")] = 1.0,
        [(Dimension.Age, "a_BP")] = 1.0,
        [(Dimension.Age, "yr")] = 1.0,
        [(Dimension.Age, "a")] = 1.0,
        [(Dimension.Age, "ka")] = 1_000.0,
        [(Dimension.Age, "ka_BP")] = 1_000.0,
        [(Dimension.Age, "Ma")] = 1_000_000.0,
        [(Dimension.Age, "Ma_BP")] = 1_000_000.0,
        [(Dimension.Offset, "yr")] = 1.0,
        [(Dimension.Offset, "a")] = 1.0,
        [(Dimension.Offset, "ka")] = 1_000.0,
        [(Dimension.Rate, "cm/yr")] = 1.0,
        [(Dimension.Rate, "cm/kyr")] = 1e-3,
        [(Dimension.Rate, "m/Myr")] = 1e-4,
        [(Dimension.Rate, "m/ka")] = 0.1,
    };

    public static (double? factor, bool needsCalibration) FactorTo(Dimension dim, string? unit)
    {
        if (unit is null) return (null, false);
        if (unit is "14C_yr_BP" or "rc_yr_BP" or "BP_uncal") return (null, true);
        return Factors.TryGetValue((dim, unit), out var f) ? (f, false) : (null, false);
    }
}

/// <summary>Project depth datum table: a depth value is only trustworthy relative to a declared datum.</summary>
public static class DatumRegistry
{
    private static readonly HashSet<string> Known = new(StringComparer.Ordinal)
    {
        "SEAFLOOR", "MCD_ZERO", "CORE_TOP", "KB", "MSL"
    };
    public static bool IsKnown(string datum) => Known.Contains(datum);
}
