namespace Chrono.Core.Materials;

internal static class Statistics
{
    public static double Median(IReadOnlyList<double> values)
    {
        if (values.Count == 0) return double.NaN;
        var sorted = values.OrderBy(v => v).ToList();
        int mid = sorted.Count / 2;
        return sorted.Count % 2 == 1 ? sorted[mid] : (sorted[mid - 1] + sorted[mid]) / 2.0;
    }

    public static double Probit(double p)
    {
        p = Math.Clamp(p, 1e-12, 1 - 1e-12);
        return NormalCdfInverse(p);
    }

    // Acklam's inverse normal CDF.
    private static double NormalCdfInverse(double p)
    {
        double[] a = { -3.969683028665376e+01, 2.209460984245205e+02, -2.759285104469687e+02,
                       1.383577518672690e+02, -3.066479806614716e+01, 2.506628277459239e+00 };
        double[] b = { -5.447609879822406e+01, 1.615858368580409e+02, -1.556989798598866e+02,
                        6.680131188771972e+01, -1.328068155288572e+01 };
        double[] c = { -7.784894002430293e-03, -3.223964580411365e-01, -2.400758277161838e+00,
                       -2.549732539343734e+00, 4.374664141464968e+00, 2.938163982698783e+00 };
        double[] d = { 7.784695709041462e-03, 3.224671290700398e-01, 2.445134137142996e+00,
                       3.754408661907416e+00 };
        double plow = 0.02425, phigh = 1 - plow, q, r, x;
        if (p < plow)
        {
            q = Math.Sqrt(-2 * Math.Log(p));
            x = (((((c[0] * q + c[1]) * q + c[2]) * q + c[3]) * q + c[4]) * q + c[5]) /
                ((((d[0] * q + d[1]) * q + d[2]) * q + d[3]) * q + 1);
        }
        else if (p <= phigh)
        {
            q = p - 0.5; r = q * q;
            x = (((((a[0] * r + a[1]) * r + a[2]) * r + a[3]) * r + a[4]) * r + a[5]) * q /
                (((((b[0] * r + b[1]) * r + b[2]) * r + b[3]) * r + b[4]) * r + 1);
        }
        else
        {
            q = Math.Sqrt(-2 * Math.Log(1 - p));
            x = -(((((c[0] * q + c[1]) * q + c[2]) * q + c[3]) * q + c[4]) * q + c[5]) /
                 ((((d[0] * q + d[1]) * q + d[2]) * q + d[3]) * q + 1);
        }
        return x;
    }

    // Quantile of Student's t via inverse regularized incomplete beta (bisection on CDF).
    public static double StudentTQuantile(double p, double df)
    {
        if (Math.Abs(p - 0.5) < 1e-12) return 0;
        double lo = -1000, hi = 1000;
        for (int i = 0; i < 80; i++)
        {
            double mid = (lo + hi) / 2;
            if (StudentTCdf(mid, df) < p) lo = mid; else hi = mid;
        }
        return (lo + hi) / 2;
    }

    private static double StudentTCdf(double t, double df)
    {
        double x = df / (df + t * t);
        double ib = RegularizedIncompleteBeta(x, df / 2, 0.5);
        return t >= 0 ? 1 - 0.5 * ib : 0.5 * ib;
    }

    private static double RegularizedIncompleteBeta(double x, double a, double b)
    {
        if (x <= 0) return 0;
        if (x >= 1) return 1;
        // Lentz continued fraction for the incomplete beta.
        double bt = Math.Exp(LogGamma(a + b) - LogGamma(a) - LogGamma(b)
                             + a * Math.Log(x) + b * Math.Log(1 - x));
        if (x < (a + 1) / (a + b + 2))
            return bt * BetaCf(x, a, b) / a;
        return 1 - bt * BetaCf(1 - x, b, a) / b;
    }

    private static double BetaCf(double x, double a, double b)
    {
        const int maxIter = 200;
        const double eps = 3e-14;
        const double fpmin = 1e-300;
        double qab = a + b, qap = a + 1, qam = a - 1;
        double c = 1, d = 1 - qab * x / qap;
        if (Math.Abs(d) < fpmin) d = fpmin;
        d = 1 / d;
        double h = d;
        for (int m = 1; m <= maxIter; m++)
        {
            int m2 = 2 * m;
            double aa = m * (b - m) * x / ((qam + m2) * (a + m2));
            d = 1 + aa * d; if (Math.Abs(d) < fpmin) d = fpmin;
            c = 1 + aa / c; if (Math.Abs(c) < fpmin) c = fpmin;
            d = 1 / d; h *= d * c;
            aa = -(a + m) * (qab + m) * x / ((a + m2) * (qap + m2));
            d = 1 + aa * d; if (Math.Abs(d) < fpmin) d = fpmin;
            c = 1 + aa / c; if (Math.Abs(c) < fpmin) c = fpmin;
            d = 1 / d;
            double del = d * c;
            h *= del;
            if (Math.Abs(del - 1) < eps) break;
        }
        return h;
    }

    private static double LogGamma(double x)
    {
        double[] coef = { 76.18009172947146, -86.50532032941677, 24.01409824083091,
                          -1.231739572450155, 0.1208650973866179e-2, -0.5395239384953e-5 };
        double y = x, tmp = x + 5.5;
        tmp -= (x + 0.5) * Math.Log(tmp);
        double ser = 1.000000000190015;
        for (int j = 0; j < coef.Length; j++) ser += coef[j] / ++y;
        return -tmp + Math.Log(2.5066282746310005 * ser / x);
    }
}
