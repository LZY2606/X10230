namespace ChronoCore;

public static class SamplesFactory
{
    /// <summary>经验分布：直接保留样本（导入时不做聚合）。</summary>
    public static AgeSamples Empirical(IEnumerable<double> calABP, bool longTail = false)
        => new() { SamplesCalABP = calABP.ToArray(), DistributionType = "empirical", LongTail = longTail };

    /// <summary>正态分布，用确定性低差异网格采样（可重放）。</summary>
    public static AgeSamples Normal(double mean, double sd, int n = 401)
        => new()
        {
            DistributionType = "normal",
            SamplesCalABP = Grid(n, p => mean + NormalQuantile(p) * sd)
        };

    /// <summary>学生 t 长尾分布：df 越小尾越长，确定性网格采样。</summary>
    public static AgeSamples StudentT(double location, double scale, int df, int n = 601)
        => new()
        {
            DistributionType = "student-t", DegreesOfFreedom = df, LongTail = df <= 8,
            SamplesCalABP = Grid(n, p => location + StudentTQuantile(p, df) * scale)
        };

    private static double[] Grid(int n, Func<double, double> f)
    {
        var a = new double[n];
        for (var i = 0; i < n; i++) a[i] = f((i + 0.5) / n);
        return a;
    }

    // Acklam 逆正态 CDF
    public static double NormalQuantile(double p)
    {
        p = Math.Clamp(p, 1e-12, 1 - 1e-12);
        double[] a = { -3.969683028665376e+01, 2.209460984245205e+02, -2.759285104469687e+02,
            1.383577518672690e+02, -3.066479806614716e+01, 2.506628277459239e+00 };
        double[] b = { -5.447609879822406e+01, 1.615858368580409e+02, -1.556989798598866e+02,
            6.680131188771972e+01, -1.328068155288572e+01 };
        double[] c = { -7.784894002430293e-03, -3.223964580411365e-01, -2.400758277161838e+00,
            -2.549732539343734e+00, 4.374664141464968e+00, 2.938163982698783e+00 };
        double[] d = { 7.784695709041462e-03, 3.224671290700398e-01, 2.445134137142996e+00, 3.754408661907416e+00 };
        const double plow = 0.02425, phigh = 1 - plow;
        double q, r;
        if (p < plow)
        {
            q = Math.Sqrt(-2 * Math.Log(p));
            return (((((c[0] * q + c[1]) * q + c[2]) * q + c[3]) * q + c[4]) * q + c[5]) /
                   ((((d[0] * q + d[1]) * q + d[2]) * q + d[3]) * q + 1);
        }
        if (p <= phigh)
        {
            q = p - 0.5; r = q * q;
            return (((((a[0] * r + a[1]) * r + a[2]) * r + a[3]) * r + a[4]) * r + a[5]) * q /
                   (((((b[0] * r + b[1]) * r + b[2]) * r + b[3]) * r + b[4]) * r + 1);
        }
        q = Math.Sqrt(-2 * Math.Log(1 - p));
        return -(((((c[0] * q + c[1]) * q + c[2]) * q + c[3]) * q + c[4]) * q + c[5]) /
                ((((d[0] * q + d[1]) * q + d[2]) * q + d[3]) * q + 1);
    }

    // Cornish-Fisher 展开近似 t 分位数
    public static double StudentTQuantile(double p, int df)
    {
        var z = NormalQuantile(p);
        var z3 = z * z * z; var z5 = z3 * z * z;
        return z + (z3 + z) / (4 * df)
                 + (5 * z5 + 16 * z3 + 3 * z) / (96.0 * df * df)
                 + (3 * z5 * z * z + 19 * z5 + 17 * z3 - 15 * z) / (384.0 * df * df * df);
    }
}
