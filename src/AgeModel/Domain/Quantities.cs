namespace AgeModel.Domain;

/// <summary>
/// A domain quantity as imported: raw representation is preserved (value + unit + datum).
/// Normalization happens only in the derived layer; unconvertible values stay in place
/// and carry a diagnostic instead of being dropped.
/// </summary>
public sealed record Quantity(double Value, string Unit, string Datum)
{
    public override string ToString() => $"{Value} {Unit} [{Datum}]";
}

public sealed record NormalizedQuantity(double Value, string CanonicalUnit, string CanonicalDatum);

public sealed record QuantityDiagnostic(string Location, string Raw, string Reason);

/// <summary>
/// Parameter versions that influence derived values. Bumping any of these changes
/// computation digests, which is how parameter revisions stay traceable.
/// </summary>
public sealed record ParamSet(string CalibrationCurve, string InterpolationRule, int CandidateBudget)
{
    public const string CurrentCalibration = "intcal-shim-2";
    public const string CurrentInterpolation = "pchip-monotone-1";
    public const int DefaultBudget = 64;

    public static ParamSet Current => new(CurrentCalibration, CurrentInterpolation, DefaultBudget);
    public string VersionTag => $"{CalibrationCurve}|{InterpolationRule}|budget={CandidateBudget}";
}

public static class UnitConverter
{
    public const string CanonicalDepthUnit = "m";
    public const string CanonicalDepthDatum = "mcd";   // meters composite depth
    public const string CanonicalAgeUnit = "ka BP";    // BP = 1950 CE, calibrated scale
    public const string CanonicalAgeDatum = "calBP1950";

    /// <summary>Depth offsets below the core datum, normalized to meters composite depth.</summary>
    public static bool TryNormalizeDepth(Quantity q, out NormalizedQuantity n, out string reason)
    {
        n = default!; reason = "";
        double factor;
        switch (q.Unit.Trim().ToLowerInvariant())
        {
            case "m": factor = 1.0; break;
            case "cm": factor = 0.01; break;
            case "mm": factor = 0.001; break;
            case "ft": factor = 0.3048; break;
            default:
                reason = $"unknown depth unit '{q.Unit}'";
                return false;
        }
        // Datum shifts are explicit; unknown datums cannot be reconciled silently.
        double shift;
        switch (q.Datum.Trim().ToLowerInvariant())
        {
            case "mcd": shift = 0.0; break;
            case "mbsf": shift = 0.0; break;               // same origin in this case pack
            case "driller": shift = 0.0; break;            // driller's depth, no wireline shift recorded
            case "mcd+9.5": shift = 9.5; break;            // offset hole datum
            default:
                reason = $"unknown depth datum '{q.Datum}'";
                return false;
        }
        n = new NormalizedQuantity(q.Value * factor + shift, CanonicalDepthUnit, CanonicalDepthDatum);
        return true;
    }

    /// <summary>
    /// Ages normalize to calibrated ka BP (1950). Radiocarbon ages require the
    /// calibration curve named by the parameter set; reservoir offsets are applied
    /// by the caller as distributions, never as bare means.
    /// </summary>
    public static bool TryNormalizeAge(Quantity q, ParamSet ps, out NormalizedQuantity n, out string reason)
    {
        n = default!; reason = "";
        string unit = q.Unit.Trim().ToLowerInvariant();
        if (unit is "ka bp" or "cal ka bp")
        {
            n = new NormalizedQuantity(q.Value, CanonicalAgeUnit, CanonicalAgeDatum);
            return true;
        }
        if (unit is "14c ka bp" or "14c ka")
        {
            // Deterministic shim standing in for a calibration curve; versioned so a
            // curve swap is a visible parameter revision, not a silent edit.
            double cal = ps.CalibrationCurve switch
            {
                "intcal-shim-1" => 1.10 * q.Value + 0.35,
                "intcal-shim-2" => 1.13 * q.Value + 0.30,
                _ => double.NaN
            };
            if (double.IsNaN(cal))
            {
                reason = $"unknown calibration curve '{ps.CalibrationCurve}'";
                return false;
            }
            n = new NormalizedQuantity(cal, CanonicalAgeUnit, CanonicalAgeDatum);
            return true;
        }
        reason = $"unknown age unit '{q.Unit}'";
        return false;
    }

    /// <summary>Reservoir offsets normalize to years (a).</summary>
    public static bool TryNormalizeOffsetYears(Quantity q, out double years, out string reason)
    {
        years = 0; reason = "";
        switch (q.Unit.Trim().ToLowerInvariant())
        {
            case "a": case "yr": case "years": years = q.Value; return true;
            case "ka": years = q.Value * 1000.0; return true;
            default: reason = $"unknown offset unit '{q.Unit}'"; return false;
        }
    }
}
