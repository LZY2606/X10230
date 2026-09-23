namespace DepthAge.Domain;

/// <summary>
/// Deterministic numeric helpers: inverse normal CDF (Acklam), Student-t quantiles
/// (bisection over the regularized incomplete beta), log-gamma (Lanczos) and
/// hash-seeded permutations (SplitMix64). No wall-clock randomness anywhere.
/// </summary>
public static class Sampling
{
    public static double LogGamma(double x)
    {
        double[] c =
        [
            676.5203681218851, -1259.1392167224028, 771.32342877765313,
            -176.61502916214059, 12.507343278686905, -0.13857109526572012,
            9.9843695780195716e-6, 1.5056327351493116e-7
        ];
        if (x < 0.5)
            return Math.Log(Math.PI / Math.Sin(Math.PI * x)) - LogGamma(1 - x);
        x -= 1;
        var acc = 0.99999999999980993;
        for (var i = 0; i < c.Length; i++)
            acc += c[i] / (x + i + 1);
        var t = x + c.Length - 0.5;
        return 0.5 * Math.Log(2 * Math.PI) + (x + 0.5) * Math.Log(t) - t + Math.Log(acc);
    }

    /// <summary>Acklam's rational approximation of the inverse standard normal CDF.</summary>
    public static double InverseNormalCdf(double p)
    {
        if (p <= 0) return double.NegativeInfinity;
        if (p >= 1) return double.PositiveInfinity;
        double[] a = [-3.969683028665376e+01, 2.209460984245205e+02, -2.759285104469687e+02,
                      1.383577518672690e+02, -3.066479806614716e+01, 2.506628277459239e+00];
        double[] b = [-5.447609879822406e+01, 1.615858368580409e+02, -1.556989798598866e+02,
                      6.680131188771972e+01, -1.328068155288572e+01];
        double[] c = [-7.784894002430293e-03, -3.223964580411365e-01, -2.400758277161838e+00,
                      -2.549732539343734e+00, 4.374664141464968e+00, 2.938163982698783e+00];
        double[] d = [7.784695709041462e-03, 3.224671290700398e-01, 2.445134137142996e+00,
                      3.754408661907416e+00];
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

    private static double BetaContinuedFraction(double a, double b, double x)
    {
        const int maxIter = 200;
        const double eps = 3e-14, fpmin = 1e-300;
        var qab = a + b;
        var qap = a + 1;
        var qam = a - 1;
        var cc = 1.0;
        var dd = 1 - qab * x / qap;
        if (Math.Abs(dd) < fpmin) dd = fpmin;
        dd = 1 / dd;
        var h = dd;
        for (var m = 1; m <= maxIter; m++)
        {
            var m2 = 2 * m;
            var aa = m * (b - m) * x / ((qam + m2) * (a + m2));
            dd = 1 + aa * dd;
            if (Math.Abs(dd) < fpmin) dd = fpmin;
            cc = 1 + aa / cc;
            if (Math.Abs(cc) < fpmin) cc = fpmin;
            dd = 1 / dd;
            h *= dd * cc;
            aa = -(a + m) * (qab + m) * x / ((a + m2) * (qap + m2));
            dd = 1 + aa * dd;
            if (Math.Abs(dd) < fpmin) dd = fpmin;
            cc = 1 + aa / cc;
            if (Math.Abs(cc) < fpmin) cc = fpmin;
            dd = 1 / dd;
            var del = dd * cc;
            h *= del;
            if (Math.Abs(del - 1) < eps) break;
        }
        return h;
    }

    /// <summary>Regularized incomplete beta I_x(a,b).</summary>
    public static double RegularizedBeta(double x, double a, double b)
    {
        if (x <= 0) return 0;
        if (x >= 1) return 1;
        var bt = Math.Exp(LogGamma(a + b) - LogGamma(a) - LogGamma(b)
                          + a * Math.Log(x) + b * Math.Log(1 - x));
        if (x < (a + 1) / (a + b + 2))
            return bt * BetaContinuedFraction(a, b, x) / a;
        return 1 - bt * BetaContinuedFraction(b, a, 1 - x) / b;
    }

    public static double StudentTCdf(double t, double nu)
    {
        var x = nu / (nu + t * t);
        var ib = RegularizedBeta(x, nu / 2, 0.5);
        return t >= 0 ? 1 - 0.5 * ib : 0.5 * ib;
    }

    /// <summary>Student-t quantile by bisection; accurate enough for stratified draws.</summary>
    public static double StudentTQuantile(double p, double nu)
    {
        if (p <= 0) return double.NegativeInfinity;
        if (p >= 1) return double.PositiveInfinity;
        var lo = -1e7;
        var hi = 1e7;
        for (var i = 0; i < 200; i++)
        {
            var mid = (lo + hi) / 2;
            if (StudentTCdf(mid, nu) < p) lo = mid; else hi = mid;
        }
        return (lo + hi) / 2;
    }

    /// <summary>SplitMix64 — deterministic 64-bit mixing for seeded permutations.</summary>
    public static ulong SplitMix64(ulong x)
    {
        x += 0x9E3779B97F4A7C15UL;
        x = (x ^ (x >> 30)) * 0xBF58476D1CE4E5B9UL;
        x = (x ^ (x >> 27)) * 0x94D049BB133111EBUL;
        return x ^ (x >> 31);
    }

    /// <summary>
    /// Deterministic permutation of [0,n) seeded by a double (e.g. a distribution median).
    /// Used to decorrelate paired stratified draws while keeping runs reproducible.
    /// </summary>
    public static int[] Permutation(int n, double seed)
    {
        var rng = SplitMix64((ulong)BitConverter.DoubleToInt64Bits(seed));
        var perm = Enumerable.Range(0, n).ToArray();
        for (var i = n - 1; i > 0; i--)
        {
            rng = SplitMix64(rng);
            var j = (int)(rng % (ulong)(i + 1));
            (perm[i], perm[j]) = (perm[j], perm[i]);
        }
        return perm;
    }
}
