using System.Text.Json.Serialization;

namespace StratAge.Core;

/// <summary>
/// An age anchor carries a full distribution. The derived layer propagates
/// samples so a long-tailed / multimodal anchor is never collapsed to a mean.
/// All ages are normalised to ka BP on the derived layer; the raw
/// representation stays on the material record.
/// </summary>
public abstract record AgeDistribution
{
    [JsonPropertyName("type")] public abstract string Type { get; }

    /// <summary>Deterministic, replay-stable samples in ka BP.</summary>
    public abstract double[] Sample(int n, ulong stream);

    public static double QuantileSorted(double[] asc, double q)
    {
        if (asc.Length == 0) return double.NaN;
        if (asc.Length == 1) return asc[0];
        var pos = Math.Clamp(q, 0, 1) * (asc.Length - 1);
        var lo = (int)Math.Floor(pos);
        var hi = (int)Math.Ceiling(pos);
        if (lo == hi) return asc[lo];
        return asc[lo] + (asc[hi] - asc[lo]) * (pos - lo);
    }

    public static double[] ResampleSorted(AgeDistribution d, int n, ulong stream)
    {
        var s = d.Sample(n, stream);
        Array.Sort(s);
        return s;
    }
}

public sealed record GaussianAge : AgeDistribution
{
    [JsonPropertyName("type")] public override string Type => "gaussian";
    [JsonPropertyName("meanKa")] public required double MeanKa { get; init; }
    [JsonPropertyName("sigmaKa")] public required double SigmaKa { get; init; }
    [JsonPropertyName("longTail")] public bool LongTail => false;

    public override double[] Sample(int n, ulong stream)
    {
        var outp = new double[n];
        for (var i = 0; i < n; i++)
        {
            var (z1, _) = Deterministic.Gaussian(stream, (ulong)i);
            outp[i] = MeanKa + z1 * SigmaKa;
        }
        return outp;
    }
}

/// <summary>Student-t distribution: explicitly long-tailed (df &lt;= 4).</summary>
public sealed record StudentTAge : AgeDistribution
{
    [JsonPropertyName("type")] public override string Type => "studentt";
    [JsonPropertyName("meanKa")] public required double MeanKa { get; init; }
    [JsonPropertyName("scaleKa")] public required double ScaleKa { get; init; }
    [JsonPropertyName("df")] public required double Df { get; init; }
    [JsonPropertyName("longTail")] public bool LongTail => Df <= 4.0;

    public override double[] Sample(int n, ulong stream)
    {
        var outp = new double[n];
        for (var i = 0; i < n; i++)
        {
            var (z1, _) = Deterministic.Gaussian(stream, (ulong)i);
            var u1 = Deterministic.Uniform01(stream, (ulong)i, 7);
            var u2 = Deterministic.Uniform01(stream, (ulong)i, 8);
            var chi2 = ChiSquaredInverse(u1, Df);
            var t = z1 / Math.Sqrt(chi2 / Df);
            outp[i] = MeanKa + t * ScaleKa;
        }
        return outp;
    }

    // Wilson-Hilferty approximation to a chi-square quantile.
    private static double ChiSquaredInverse(double p, double df)
    {
        var z = Deterministic.NormalQuantile(p);
        var h = 2.0 / (9.0 * df);
        var x = 1.0 - h + z * Math.Sqrt(h);
        return df * x * x * x;
    }
}

/// <summary>
/// Empirical distribution (e.g. a reservoir-corrected calibration curve or
/// imported MCMC draws). Samples are preserved verbatim.
/// </summary>
public sealed record EmpiricalAge : AgeDistribution
{
    [JsonPropertyName("type")] public override string Type => "empirical";
    [JsonPropertyName("samplesKa")] public required double[] SamplesKa { get; init; }

    [JsonPropertyName("longTail")]
    public bool LongTail
    {
        get
        {
            if (SamplesKa.Length < 8) return false;
            var c = (double[])SamplesKa.Clone();
            Array.Sort(c);
            var q50 = AgeDistribution.QuantileSorted(c, 0.5);
            var q99 = AgeDistribution.QuantileSorted(c, 0.99);
            var q1 = AgeDistribution.QuantileSorted(c, 0.01);
            return q99 - q50 > 3.0 * Math.Max(q50 - q1, 1e-9);
        }
    }

    public override double[] Sample(int n, ulong stream)
    {
        var src = SamplesKa;
        var sorted = (double[])src.Clone();
        Array.Sort(sorted);
        var outp = new double[n];
        for (var i = 0; i < n; i++)
        {
            var u = Deterministic.Uniform01(stream, (ulong)i, 11);
            outp[i] = QuantileSorted(sorted, u);
        }
        return outp;
    }
}

/// <summary>Reservoir / marine offset, itself possibly uncertain.</summary>
public sealed record ReservoirOffset
{
    [JsonPropertyName("id")] public required string Id { get; init; }
    [JsonPropertyName("raw")] public required string Raw { get; init; }
    [JsonPropertyName("deltaRKa")] public required double DeltaRKa { get; init; }
    [JsonPropertyName("sigmaKa")] public required double SigmaKa { get; init; }
    [JsonPropertyName("basis")] public required string Basis { get; init; }

    public GaussianAge Distribution() => new() { MeanKa = DeltaRKa, SigmaKa = SigmaKa };
}

/// <summary>
/// Deterministic pseudo-random primitives so the same anchor/stream always
/// produces identical samples (replay &amp; stable ordering).
/// </summary>
public static class Deterministic
{
    private static double ToUnit(ulong x)
    {
        // SplitMix64 mixing, mapped to (0,1).
        x += 0x9E3779B97F4A7C15UL;
        x = (x ^ (x >> 30)) * 0xBF58476D1CE4E5B9UL;
        x = (x ^ (x >> 27)) * 0x94D049BB133111EBUL;
        x ^= x >> 31;
        return (x >> 11) * (1.0 / 9007199254740992.0);
    }

    public static double Uniform01(ulong stream, ulong index, ulong salt)
    {
        var x = stream * 0xD1B54A32D192ED03UL ^ index * 0xA24BAED4963EE407UL ^
                salt * 0x9E3779B97F4A7C15UL;
        var u = ToUnit(x);
        return Math.Clamp(u, 1e-12, 1 - 1e-12);
    }

    public static double NormalQuantile(double p)
    {
        // Acklam's inverse normal CDF.
        const double a1 = -39.6968302866538, a2 = 220.946098424521, a3 = -275.928510446969;
        const double a4 = 138.357751867269, a5 = -30.6647980661472, a6 = 2.50662827745924;
        const double b1 = -54.4760987982241, b2 = 161.585836858041, b3 = -155.698979859887;
        const double b4 = 66.8013118877197, b5 = -13.2806815528857;
        const double c1 = -0.00778489400243029, c2 = -0.322396458041136, c3 = -2.40075827716184;
        const double c4 = -2.54973253934373, c5 = 4.37466414146497, c6 = 2.93816398269878;
        const double d1 = 0.00778469570904147, d2 = 0.32246712907004, d3 = 2.445134137143;
        const double d4 = 3.75440866190742;
        const double plow = 0.02425, phigh = 1 - plow;
        double q, r;
        if (p < plow)
        {
            q = Math.Sqrt(-2 * Math.Log(p));
            return (((((c1 * q + c2) * q + c3) * q + c4) * q + c5) * q + c6) /
                   ((((d1 * q + d2) * q + d3) * q + d4) * q + 1);
        }
        if (p <= phigh)
        {
            q = p - 0.5; r = q * q;
            return (((((a1 * r + a2) * r + a3) * r + a4) * r + a5) * r + a6) * q /
                   (((((b1 * r + b2) * r + b3) * r + b4) * r + b5) * r + 1);
        }
        q = Math.Sqrt(-2 * Math.Log(1 - p));
        return -(((((c1 * q + c2) * q + c3) * q + c4) * q + c5) * q + c6) /
                ((((d1 * q + d2) * q + d3) * q + d4) * q + 1);
    }

    public static (double z1, double z2) Gaussian(ulong stream, ulong index)
    {
        var u1 = Uniform01(stream, index, 1);
        var u2 = Uniform01(stream, index, 2);
        var r = Math.Sqrt(-2.0 * Math.Log(u1));
        var th = 2.0 * Math.PI * u2;
        return (r * Math.Cos(th), r * Math.Sin(th));
    }
}
