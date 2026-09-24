namespace ChronoCore;

/// <summary>标准正态工具：CDF、有理近似逆 CDF（Acklam）、确定性 Box-Muller 抽样。</summary>
public static class Gaussian
{
    public static double Cdf(double x) => 0.5 * Erfc(-x / Math.Sqrt(2));

    public static double Sample(System.Random rng)
    {
        double u1, u2;
        do { u1 = rng.NextDouble(); } while (u1 <= 1e-15);
        u2 = rng.NextDouble();
        return Math.Sqrt(-2.0 * Math.Log(u1)) * Math.Cos(2.0 * Math.PI * u2);
    }

    // Peter J. Acklam 有理近似，|eps| < ~1.15e-9。
    private static readonly double[] A =
        {-3.969683028665376e+01, 2.209460984245205e+02, -2.759285104469687e+02,
         1.383577518672690e+02, -3.066479806614716e+01, 2.506628277459239e+00};
    private static readonly double[] B =
        {-5.447609879822406e+01, 1.615858368580409e+02, -1.556989798598866e+02,
         6.680131188771972e+01, -1.328068155288572e+01};
    private static readonly double[] C =
        {-7.784894002430293e-03, -3.223964580411365e-01, -2.400758277161838e+00,
         -2.549732539343734e+00, 4.374664141464968e+00, 2.938163982698783e+00};
    private static readonly double[] D =
        {7.784695709041462e-03, 3.224671290700398e-01, 2.445134137142996e+00,
         3.754408661907416e+00};

    public static double InverseCdf(double p)
    {
        if (p is <= 0 or >= 1)
            throw new ArgumentOutOfRangeException(nameof(p), "quantile probability must be in (0,1)");
        const double pLow = 0.02425;
        const double pHigh = 1 - pLow;
        double q, r;
        if (p < pLow)
        {
            q = Math.Sqrt(-2 * Math.Log(p));
            return (((((C[0] * q + C[1]) * q + C[2]) * q + C[3]) * q + C[4]) * q + C[5]) /
                   ((((D[0] * q + D[1]) * q + D[2]) * q + D[3]) * q + 1);
        }
        if (p <= pHigh)
        {
            q = p - 0.5; r = q * q;
            return (((((A[0] * r + A[1]) * r + A[2]) * r + A[3]) * r + A[4]) * r + A[5]) * q /
                   (((((B[0] * r + B[1]) * r + B[2]) * r + B[3]) * r + B[4]) * r + 1);
        }
        q = Math.Sqrt(-2 * Math.Log(1 - p));
        return -(((((C[0] * q + C[1]) * q + C[2]) * q + C[3]) * q + C[4]) * q + C[5]) /
                ((((D[0] * q + D[1]) * q + D[2]) * q + D[3]) * q + 1);
    }

    private static double Erfc(double x)
    {
        // Abramowitz-Stegun 7.1.26
        var sign = x < 0 ? -1.0 : 1.0;
        x = Math.Abs(x);
        var t = 1.0 / (1.0 + 0.3275911 * x);
        var y = 1.0 - (((((1.061405429 * t - 1.453152027) * t) + 1.421413741) * t
                       - 0.284496736) * t + 0.254829592) * t * Math.Exp(-x * x);
        return 1 - sign * y;
    }
}
