namespace AgeModel.Core.Distributions;

/// <summary>Scaled, shifted Student's t distribution (long tails).</summary>
public sealed record StudentTDistribution(double Mu, double Scale, double Df) : AgeDistribution
{
    public override double Mean => Df > 1 ? Mu : double.NaN;

    public override double Sample(DeterministicRng rng)
    {
        // t = Z / sqrt(V/df); chi-square via gamma shape df/2 (Marsaglia-Tsang)
        double z = rng.NextNormal();
        double u = GammaSample(rng, Df / 2.0);
        double t = z / Math.Sqrt(2.0 * u / Df);
        return Mu + Scale * t;
    }

    internal static double GammaSample(DeterministicRng rng, double shape)
    {
        if (shape < 1)
        {
            double u = Math.Max(rng.NextDouble(), 1e-15);
            return GammaSample(rng, shape + 1) * Math.Pow(u, 1.0 / shape);
        }
        double d = shape - 1.0 / 3.0, c = 1.0 / Math.Sqrt(9.0 * d);
        while (true)
        {
            double x, v;
            do { x = rng.NextNormal(); v = 1 + c * x; } while (v <= 0);
            v = v * v * v;
            double u = Math.Max(rng.NextDouble(), 1e-15);
            if (u < 1 - 0.0331 * x * x * x * x) return d * v;
            if (Math.Log(u) < 0.5 * x * x + d * (1 - v + Math.Log(v))) return d * v;
        }
    }

    public override double Quantile(double p)
    {
        // bisection on numerical CDF computed from regularized incomplete beta
        double lo = Mu - Scale * 1e4, hi = Mu + Scale * 1e4;
        for (int i = 0; i < 80; i++)
        {
            double mid = (lo + hi) / 2;
            double t = (mid - Mu) / Scale;
            double x = Df / (Df + t * t);
            double ib = IncompleteBeta(x, Df / 2.0, 0.5);
            double cdf = t >= 0 ? 1 - 0.5 * ib : 0.5 * ib;
            if (cdf < p) lo = mid; else hi = mid;
        }
        return (lo + hi) / 2;
    }

    public double Variance() => Df > 2 ? Scale * Scale * Df / (Df - 2) : double.PositiveInfinity;

    public override string Describe() => $"t(μ={Quantities.UnitCatalog.Format(Mu)}, s={Quantities.UnitCatalog.Format(Scale)}, df={Quantities.UnitCatalog.Format(Df)})";

    // Lentz regularized incomplete beta I_x(a,b)
    internal static double IncompleteBeta(double x, double a, double b)
    {
        if (x is <= 0 or >= 1) return x <= 0 ? 0 : 1;
        double lbeta = GammaLn(a) + GammaLn(b) - GammaLn(a + b);
        double front = Math.Exp(Math.Log(x) * a + Math.Log(1 - x) * b - lbeta) / a;
        if (x < (a + 1) / (a + b + 2)) return front * Betacf(x, a, b);
        return 1 - Math.Exp(Math.Log(1 - x) * b + Math.Log(x) * a - lbeta) / b * Betacf(1 - x, b, a);
    }

    private static double Betacf(double x, double a, double b)
    {
        const int itmax = 200; const double eps = 3e-12, fpmin = 1e-300;
        double qab = a + b, qap = a + 1, qam = a - 1;
        double c = 1, d = 1 - qab * x / qap;
        if (Math.Abs(d) < fpmin) d = fpmin;
        d = 1 / d; double h = d;
        for (int m = 1; m <= itmax; m++)
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
            double del = d * c; h *= del;
            if (Math.Abs(del - 1) < eps) break;
        }
        return h;
    }

    private static double GammaLn(double z)
    {
        double[] co = { 76.18009172947146, -86.50532032941677, 24.01409824083091, -1.231739572450155,
            0.1208650973866179e-2, -0.5395239384953e-5 };
        double x = z, y = z;
        double tmp = x + 5.5; tmp -= (x + 0.5) * Math.Log(tmp);
        double ser = 1.000000000190015;
        for (int j = 0; j < 6; j++) ser += co[j] / ++y;
        return -tmp + Math.Log(2.5066282746310005 * ser / x);
    }
}
