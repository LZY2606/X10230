using System.Security.Cryptography;
using System.Text;

namespace AgeModel.Domain;

public static class Hash
{
    public static string Sha(string s) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(s))).ToLowerInvariant();
}

/// <summary>
/// Unit normalization. Depths normalize to mbsf (metres below seafloor),
/// ages to ka BP. Raw representation is always preserved on the entity;
/// unconvertible values keep Value = null and stay in place with a diagnostic.
/// </summary>
public static class Units
{
    public const string DepthDatum = "mbsf";
    public const string AgeUnit = "kaBP";

    public static bool TryNormalizeDepth(double value, string? unit, out double normalized)
    {
        normalized = value;
        switch ((unit ?? "").Trim().ToLowerInvariant())
        {
            case "mbsf": case "m": return true;
            case "cmbsf": case "cm": normalized = value / 100.0; return true;
            case "mm": normalized = value / 1000.0; return true;
            default: return false;
        }
    }

    public static bool TryNormalizeAge(double value, string? unit, out double normalized)
    {
        normalized = value;
        switch ((unit ?? "").Trim().ToLowerInvariant())
        {
            case "kabp": case "ka": case "calkabp": return true;
            case "abp": case "bp": case "yrbp": case "calbp": normalized = value / 1000.0; return true;
            case "ma": normalized = value * 1000.0; return true;
            default: return false;
        }
    }
}

/// <summary>A quantity carrying its unit/datum plus the original raw text.</summary>
public sealed record Quantity(double? Value, string Unit, string Raw)
{
    public string Display => Value.HasValue ? $"{Value.Value:0.###} {Unit}" : $"{Raw} [unconverted:{Unit}]";
}

/// <summary>
/// Age distribution of one anchor. Quantiles are the only sanctioned way to
/// derive point estimates; a distribution is never replaced by its mean.
/// </summary>
public sealed record AgeDistribution(
    string Kind,          // normal | uniform | samples
    double? Mean,
    double? Sigma,
    double? Min,
    double? Max,
    double[]? Samples,    // normalized ka BP
    string Unit,
    string Raw)
{
    public double Quantile(double q) => Kind switch
    {
        "normal" => (Mean ?? 0) + (Sigma ?? 0) * NormalInv(q),
        "uniform" => (Min ?? 0) + q * ((Max ?? 0) - (Min ?? 0)),
        "samples" => SampleQuantile(Samples ?? Array.Empty<double>(), q),
        _ => double.NaN
    };

    public double P05 => Quantile(0.05);
    public double Median => Quantile(0.5);
    public double P95 => Quantile(0.95);

    public double MeanValue => Kind switch
    {
        "normal" => Mean ?? double.NaN,
        "uniform" => ((Min ?? 0) + (Max ?? 0)) / 2.0,
        "samples" => Samples is { Length: > 0 } s ? s.Average() : double.NaN,
        _ => double.NaN
    };

    public string Canonical => Kind switch
    {
        "normal" => $"normal({Mean:0.######},{Sigma:0.######})",
        "uniform" => $"uniform({Min:0.######},{Max:0.######})",
        "samples" => "samples(" + string.Join(",", (Samples ?? Array.Empty<double>()).Select(x => x.ToString("0.######"))) + ")",
        _ => "unknown"
    };

    static double SampleQuantile(double[] s, double q)
    {
        if (s.Length == 0) return double.NaN;
        var sorted = s.OrderBy(x => x).ToArray();
        var pos = q * (sorted.Length - 1);
        var lo = (int)Math.Floor(pos);
        var hi = Math.Min(lo + 1, sorted.Length - 1);
        return sorted[lo] + (sorted[hi] - sorted[lo]) * (pos - lo);
    }

    // Acklam's inverse normal CDF approximation.
    static double NormalInv(double p)
    {
        double[] a = { -3.969683028665376e+01, 2.209460984245205e+02, -2.759285104469687e+02, 1.383577518672690e+02, -3.066479806614716e+01, 2.506628277459239e+00 };
        double[] b = { -5.447609879822406e+01, 1.615858368580409e+02, -1.556989798598866e+02, 6.680131188771972e+01, -1.328068155288572e+01 };
        double[] c = { -7.784894002430293e-03, -3.223964580411365e-01, -2.400758277161838e+00, -2.549732539343734e+00, 4.374664141464968e+00, 2.938163982698783e+00 };
        double[] d = { 7.784695709041462e-03, 3.224671290700398e-01, 2.445134137142996e+00, 3.754408661907416e+00 };
        const double plow = 0.02425, phigh = 1 - plow;
        if (p < plow)
        {
            var q = Math.Sqrt(-2 * Math.Log(p));
            return (((((c[0] * q + c[1]) * q + c[2]) * q + c[3]) * q + c[4]) * q + c[5]) /
                   ((((d[0] * q + d[1]) * q + d[2]) * q + d[3]) * q + 1);
        }
        if (p > phigh)
        {
            var q = Math.Sqrt(-2 * Math.Log(1 - p));
            return -(((((c[0] * q + c[1]) * q + c[2]) * q + c[3]) * q + c[4]) * q + c[5]) /
                     ((((d[0] * q + d[1]) * q + d[2]) * q + d[3]) * q + 1);
        }
        var qq = p - 0.5;
        var r = qq * qq;
        return (((((a[0] * r + a[1]) * r + a[2]) * r + a[3]) * r + a[4]) * r + a[5]) * qq /
               (((((b[0] * r + b[1]) * r + b[2]) * r + b[3]) * r + b[4]) * r + 1);
    }
}

// ---- Immutable materials (import layer; raw representation preserved) ----

public sealed record Section(string Id, int Ord, Quantity Top, Quantity Bottom, string BoundaryAbove); // none|gap|unconformity

public sealed record Anchor(string Id, int Ord, string SectionId, Quantity Depth, AgeDistribution? Dist, string DistRaw, bool Marine, string Note);

public sealed record ReservoirOffset(string Id, int Ord, double? OffsetKa, string Unit, string Raw, string AppliesTo);

public sealed record EventLayer(string Id, int Ord, string Name, string SectionId, Quantity Depth, string[] CorrelatedAnchorIds);

public sealed record ExclusionDecision(string Id, int Ord, string AnchorId, string Reason, string Reviewer);
