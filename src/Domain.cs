using System.Globalization;

namespace AgeDepth;

/// <summary>A normalized quantity: value in a canonical unit plus its coordinate datum.</summary>
public sealed record Quantity(double Value, string Unit, string Datum);

/// <summary>
/// A quantity as imported. The original textual representation is always preserved;
/// normalization into canonical units happens only in the derived layer. Values that
/// cannot be converted stay in place with a diagnostic error instead of being dropped.
/// </summary>
public sealed record RawQuantity(string Raw, string? Unit, string? Datum)
{
    public Quantity? Normalized { get; init; }
    public string? Error { get; init; }
}

public static class Units
{
    static readonly Dictionary<string, double> DepthFactors = new()
    { ["m"] = 1.0, ["cm"] = 0.01, ["mm"] = 0.001, ["ft"] = 0.3048 };

    static readonly Dictionary<string, double> AgeFactors = new()
    { ["ka"] = 1.0, ["kyr"] = 1.0, ["yr"] = 0.001, ["a"] = 0.001, ["Ma"] = 1000.0 };

    public static RawQuantity Depth(string raw, string? unit, string? datum)
        => Normalize(raw, unit, datum, DepthFactors, "m");

    public static RawQuantity Age(string raw, string? unit, string? datum)
        => Normalize(raw, unit, datum, AgeFactors, "ka");

    static RawQuantity Normalize(string raw, string? unit, string? datum,
        Dictionary<string, double> factors, string canonicalUnit)
    {
        var u = string.IsNullOrWhiteSpace(unit) ? canonicalUnit : unit.Trim();
        if (!double.TryParse(raw.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out var v))
            return new RawQuantity(raw, u, datum) { Error = $"unparseable-value: '{raw}'" };
        if (!factors.TryGetValue(u, out var f))
            return new RawQuantity(raw, u, datum) { Error = $"unknown-unit: '{u}'" };
        return new RawQuantity(raw, u, datum)
        {
            Normalized = new Quantity(v * f, canonicalUnit, datum ?? "unspecified")
        };
    }
}

/// <summary>
/// An age distribution kept as a quantile table. The full distribution is preserved;
/// the engine works on quantiles and never substitutes the mean for the distribution.
/// </summary>
public sealed record AgeDist(string Kind, double[] P, double[] Q, string Raw)
{
    public const int N = 41;

    public double Quantile(double p)
    {
        p = Math.Clamp(p, 0.0, 1.0);
        var x = p * (N - 1);
        var i = (int)Math.Floor(x);
        if (i >= N - 1) return Q[N - 1];
        var t = x - i;
        return Q[i] * (1 - t) + Q[i + 1] * t;
    }

    public double Median => Quantile(0.5);
    public double P05 => Quantile(0.05);
    public double P95 => Quantile(0.95);

    public double Mean
    {
        get
        {
            double s = 0;
            for (var i = 0; i < N - 1; i++)
                s += (Q[i] + Q[i + 1]) / 2.0 * (P[i + 1] - P[i]);
            return s;
        }
    }

    /// <summary>Deterministic quantile-wise sum (used to apply a reservoir offset).</summary>
    public AgeDist Add(AgeDist other)
        => new("sum", P.ToArray(),
            P.Select((p, i) => Quantile(p) + other.Quantile(p)).ToArray(),
            $"{Raw} + {other.Raw}");

    public static AgeDist Normal(double mu, double sigma, string raw)
        => new("normal", Grid(), Grid().Select(p => mu + sigma * InvNorm(p)).ToArray(), raw);

    public static AgeDist Uniform(double lo, double hi, string raw)
        => new("uniform", Grid(), Grid().Select(p => lo + (hi - lo) * p).ToArray(), raw);

    /// <summary>Log-normal style long-tailed distribution: median * exp(sigmaLog * z).</summary>
    public static AgeDist LongTail(double median, double sigmaLog, string raw)
        => new("longtail", Grid(),
            Grid().Select(p => median * Math.Exp(sigmaLog * InvNorm(p))).ToArray(), raw);

    static double[] Grid() => Enumerable.Range(0, N)
        .Select(i => Math.Clamp((double)i / (N - 1), 1e-4, 1 - 1e-4)).ToArray();

    /// <summary>Acklam's inverse normal CDF approximation.</summary>
    public static double InvNorm(double p)
    {
        p = Math.Clamp(p, 1e-12, 1 - 1e-12);
        double[] a = { -3.969683028665376e+01, 2.209460984245205e+02, -2.759285104469687e+02,
                        1.383577518672690e+02, -3.066479806614716e+01, 2.506628277459239e+00 };
        double[] b = { -5.447609879822406e+01, 1.615858368580409e+02, -1.556989798598866e+02,
                        6.680131188771972e+01, -1.328068155288572e+01 };
        double[] c = { -7.784894002430293e-03, -3.223964580411365e-01, -2.400758277161838e+00,
                       -2.549732539343734e+00,  4.374664141464968e+00,  2.938163982698783e+00 };
        double[] d = { 7.784695709041462e-03, 3.224671290700398e-01, 2.445134137142996e+00,
                       3.754408661907416e+00 };
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

public enum BoundaryKind { Continuous, Gap, Unconformity }

// ---- Immutable evidence materials (versioned; a revision creates a new version) ----

public sealed record CoreMat(string Id, int Version, string Name, string Datum, string DepthUnit);

public sealed record SegmentMat(string Id, int Version, string CoreId, RawQuantity Top, RawQuantity Bottom);

public sealed record BoundaryMat(string Id, int Version, string CoreId, string AfterSegmentId,
    BoundaryKind Kind, string Note);

public sealed record AnchorMat(string Id, int Version, string CoreId, RawQuantity Depth,
    AgeDist Dist, string? ReservoirOffsetId, string? EventLayerId, string Method);

public sealed record ReservoirOffsetMat(string Id, int Version, AgeDist OffsetKa, string Note);

public sealed record EventLayerMat(string Id, int Version, string Name,
    Dictionary<string, double> DepthByCoreM, AgeDist? AgeKa, string Note);

public sealed record ExclusionMat(string Id, int Version, string AnchorId, string Reason, bool Active);

/// <summary>A positioned diagnostic. Never a silent default: position + reason always present.</summary>
public sealed record Diagnostic(string Code, string Path, string Message, string Severity);
