namespace CoreChrono.Domain;

/// <summary>Minimal deterministic statistics primitives (no third-party math dependency).
/// Probability functions are used for closed-form quantiles; sampling uses a
/// deterministic LCG in the engine so replays are byte-stable.</summary>
public static class MathNetLite
{
    public static double Erf(double x)
    {
        // Abramowitz & Stegun 7.1.26
        var sign = Math.Sign(x);
        x = Math.Abs(x);
        var a1 = 0.254829592; var a2 = -0.284496736; var a3 = 1.421413741;
        var a4 = -1.453152027; var a5 = 1.061405429; var p = 0.3275911;
        var t = 1.0 / (1.0 + p * x);
        var y = 1.0 - (((((a5 * t + a4) * t) + a3) * t + a2) * t + a1) * t * Math.Exp(-x * x);
        return sign * y;
    }

    public static double Phi(double x) => 0.5 * (1.0 + Erf(x / Math.Sqrt(2.0)));

    public static double Probit(double p)
    {
        p = Math.Clamp(p, 1e-9, 1 - 1e-9);
        // Beasley-Springer-Moro
        double[] a = { -3.969683028665376e+01, 2.209460984245205e+02, -2.759285104469687e+02,
                        1.383577518672690e+02, -3.066479806614716e+01, 2.506628277459239e+00 };
        double[] b = { -5.447609879822406e+01, 1.615858368580409e+02, -1.556989798598866e+02,
                        6.680131188771972e+01, -1.328068155288572e+01 };
        double[] c = { -7.784894002430293e-03, -3.223964580411365e-01, -2.400758277161838e+00,
                       -2.549732539343734e+00, 4.374664141464968e+00, 2.938163982698783e+00 };
        double[] d = { 7.784695709041462e-03, 3.224671290700398e-01, 2.445134137142996e+00, 3.754408661907416e+00 };
        const double pLow = 0.02425, pHigh = 1 - pLow;
        double q, r;
        if (p < pLow) { q = Math.Sqrt(-2 * Math.Log(p)); return (((((c[0] * q + c[1]) * q + c[2]) * q + c[3]) * q + c[4]) * q + c[5]) / ((((d[0] * q + d[1]) * q + d[2]) * q + d[3]) * q + 1); }
        if (p <= pHigh) { q = p - 0.5; r = q * q; return (((((a[0] * r + a[1]) * r + a[2]) * r + a[3]) * r + a[4]) * r + a[5]) * q / (((((b[0] * r + b[1]) * r + b[2]) * r + b[3]) * r + b[4]) * r + 1); }
        q = Math.Sqrt(-2 * Math.Log(1 - p));
        return -(((((c[0] * q + c[1]) * q + c[2]) * q + c[3]) * q + c[4]) * q + c[5]) / ((((d[0] * q + d[1]) * q + d[2]) * q + d[3]) * q + 1);
    }

    /// <summary>Inverse CDF of Student's t via inverse-CDF expansion around the normal.
    /// Accurate enough for a teaching/review tool's quantile bands.</summary>
    public static double TInv(double p, double df)
    {
        var z = Probit(p);
        if (df > 1e6) return z;
        var z3 = z * z * z;
        var z5 = z3 * z * z;
        var corr = (z3 + z) / (4 * df)
                 + (5 * z5 + 16 * z3 + 3 * z) / (96 * df * df)
                 + (3 * z5 * z * z + 19 * z5 + 17 * z3 - 15 * z) / (384 * df * df * df);
        return z + corr;
    }

    /// <summary>Sample CDF p in (0,1) deterministically via a splitmix stream.</summary>
    public static double NormalFromU(double u) => Probit(u);
}

/// <summary>Deterministic 64-bit PRNG (SplitMix64) so identical seed+draws always
/// reproduce the same uncertainty band and the same candidate ordering input.</summary>
public sealed class DeterministicRng
{
    private ulong _state;
    public DeterministicRng(ulong seed) => _state = seed;
    public ulong NextU64()
    {
        _state += 0x9E3779B97F4A7C15UL;
        var z = _state;
        z = (z ^ (z >> 30)) * 0xBF58476D1CE4E5B9UL;
        z = (z ^ (z >> 27)) * 0x94D049BB133111EBUL;
        return z ^ (z >> 31);
    }
    public double NextDouble() => (NextU64() >> 11) * (1.0 / 9007199254740992.0);
}
