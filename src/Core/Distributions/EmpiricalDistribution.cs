namespace AgeModel.Core.Distributions;

/// <summary>Imported empirical distribution (raw draws are preserved and sorted lazily).</summary>
public sealed record EmpiricalDistribution(IReadOnlyList<double> Draws) : AgeDistribution
{
    private readonly double[] _sorted = Draws.OrderBy(x => x).ToArray();

    public override double Mean => _sorted.Average();

    public override double Sample(DeterministicRng rng)
    {
        double u = rng.NextDouble() * (_sorted.Length - 1);
        int i = (int)u;
        double f = u - i;
        return _sorted[i] * (1 - f) + (i + 1 < _sorted.Length ? _sorted[i + 1] : _sorted[i]) * f;
    }

    public override double Quantile(double p)
    {
        double idx = Math.Clamp(p, 0, 1) * (_sorted.Length - 1);
        int i = (int)idx;
        double f = idx - i;
        return i + 1 < _sorted.Length ? _sorted[i] * (1 - f) + _sorted[i + 1] * f : _sorted[i];
    }

    public override string Describe() => $"emp(n={Draws.Count})";
}
