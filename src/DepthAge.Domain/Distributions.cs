using System.Text.Json.Serialization;

namespace DepthAge.Domain;

/// <summary>
/// An age probability distribution. Distributions are first-class domain values:
/// an anchor's distribution is never replaced by its mean. Propagation happens by
/// deterministic stratified sampling (see Sampling); summaries (median/quantiles)
/// are derived only for display and legality checks.
/// </summary>
[JsonPolymorphic(TypeDiscriminatorPropertyName = "kind")]
[JsonDerivedType(typeof(GaussianDist), "gaussian")]
[JsonDerivedType(typeof(StudentTDist), "studentT")]
[JsonDerivedType(typeof(UniformDist), "uniform")]
[JsonDerivedType(typeof(EmpiricalDist), "empirical")]
[JsonDerivedType(typeof(DifferenceDist), "difference")]
public abstract record AgeDist
{
    /// <summary>Unit of the values, e.g. "cal yr BP" or "14C yr BP".</summary>
    public required string Unit { get; init; }

    public abstract double Median();
    public abstract double Quantile(double p);
    public abstract double Pdf(double x);

    /// <summary>Deterministic stratified quantile draw: value at (k+0.5)/n.</summary>
    public double StratifiedDraw(int k, int n) => Quantile((k + 0.5) / (double)n);

    public bool IsLongTail()
    {
        var med = Median();
        var hi = Quantile(0.95) - med;
        var lo = med - Quantile(0.05);
        return hi > 3.0 * Math.Max(lo, 1e-9);
    }

    /// <summary>Subtract another distribution (e.g. reservoir offset) by paired stratified sampling.</summary>
    public AgeDist Minus(AgeDist other, int samples = 64)
    {
        if (other is GaussianDist g && this is GaussianDist self && self.Unit == g.Unit)
        {
            return new GaussianDist
            {
                Unit = self.Unit,
                Mean = self.Mean - g.Mean,
                Sd = Math.Sqrt(self.Sd * self.Sd + g.Sd * g.Sd)
            };
        }
        return new DifferenceDist { Unit = this.Unit, Left = this, Right = other, Samples = samples };
    }
}

public sealed record GaussianDist : AgeDist
{
    public required double Mean { get; init; }
    public required double Sd { get; init; }

    public override double Median() => Mean;
    public override double Quantile(double p) => Mean + Sd * Sampling.InverseNormalCdf(p);
    public override double Pdf(double x)
    {
        var z = (x - Mean) / Sd;
        return Math.Exp(-0.5 * z * z) / (Sd * Math.Sqrt(2 * Math.PI));
    }
}

public sealed record StudentTDist : AgeDist
{
    public required double Mu { get; init; }
    public required double Sigma { get; init; }
    public required double Nu { get; init; }

    public override double Median() => Mu;
    public override double Quantile(double p) => Mu + Sigma * Sampling.StudentTQuantile(p, Nu);

    public override double Pdf(double x)
    {
        var z = (x - Mu) / Sigma;
        var c = Math.Exp(Sampling.LogGamma((Nu + 1) / 2) - Sampling.LogGamma(Nu / 2))
                / (Math.Sqrt(Nu * Math.PI) * Sigma);
        return c * Math.Pow(1 + z * z / Nu, -(Nu + 1) / 2);
    }
}

public sealed record UniformDist : AgeDist
{
    public required double Min { get; init; }
    public required double Max { get; init; }

    public override double Median() => (Min + Max) / 2;
    public override double Quantile(double p) => Min + (Max - Min) * p;
    public override double Pdf(double x) => x >= Min && x <= Max ? 1.0 / (Max - Min) : 0.0;
}

/// <summary>Discrete dated-sample ensemble (e.g. tephra population ages), kept in full.</summary>
public sealed record EmpiricalDist : AgeDist
{
    public required double[] Values { get; init; }

    public override double Median() => Quantile(0.5);

    public override double Quantile(double p)
    {
        var sorted = Values.OrderBy(v => v).ToArray();
        var pos = p * (sorted.Length - 1);
        var lo = (int)Math.Floor(pos);
        var hi = Math.Min(lo + 1, sorted.Length - 1);
        return sorted[lo] + (sorted[hi] - sorted[lo]) * (pos - lo);
    }

    public override double Pdf(double x)
    {
        // Gaussian-kernel density for display only.
        var sd = Math.Max(1e-9, (Quantile(0.84) - Quantile(0.16)) / 2);
        var h = 1.06 * sd * Math.Pow(Values.Length, -0.2);
        return Values.Average(v =>
        {
            var z = (x - v) / h;
            return Math.Exp(-0.5 * z * z) / (h * Math.Sqrt(2 * Math.PI));
        });
    }
}

/// <summary>
/// Distribution of Left - Right, evaluated by deterministic paired stratified sampling.
/// Used for reservoir-offset subtraction so the full convolved shape (incl. long tails)
/// is preserved instead of collapsing to a mean shift.
/// </summary>
public sealed record DifferenceDist : AgeDist
{
    public required AgeDist Left { get; init; }
    public required AgeDist Right { get; init; }
    public required int Samples { get; init; }

    private double[] Draws()
    {
        var n = Math.Max(8, Samples);
        var leftPerm = Sampling.Permutation(n, Left.Median());
        var rightPerm = Sampling.Permutation(n, Right.Median());
        var values = new double[n];
        for (var i = 0; i < n; i++)
            values[i] = Left.StratifiedDraw(leftPerm[i], n) - Right.StratifiedDraw(rightPerm[i], n);
        Array.Sort(values);
        return values;
    }

    public override double Median() => Quantile(0.5);

    public override double Quantile(double p)
    {
        var draws = Draws();
        var pos = p * (draws.Length - 1);
        var lo = (int)Math.Floor(pos);
        var hi = Math.Min(lo + 1, draws.Length - 1);
        return draws[lo] + (draws[hi] - draws[lo]) * (pos - lo);
    }

    public override double Pdf(double x)
    {
        var draws = Draws();
        var sd = Math.Max(1e-9, (Quantile(0.84) - Quantile(0.16)) / 2);
        var h = 1.06 * sd * Math.Pow(draws.Length, -0.2);
        return draws.Average(v =>
        {
            var z = (x - v) / h;
            return Math.Exp(-0.5 * z * z) / (h * Math.Sqrt(2 * Math.PI));
        });
    }
}
