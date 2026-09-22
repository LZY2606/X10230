namespace AgeModel.Core.Distributions;

/// <summary>
/// Discrete mixture of distributions (e.g. a main population plus a reworked long-tail
/// component). Quantiles are evaluated numerically; the mixture MUST NOT be reduced to
/// its (often misleading) mean.
/// </summary>
public sealed record MixtureDistribution(IReadOnlyList<MixtureComponent> Components) : AgeDistribution
{
    private double[]? _sortedSamples;

    public MixtureDistribution(params MixtureComponent[] components) : this((IReadOnlyList<MixtureComponent>)components) { }

    public override double Mean => Components.Sum(c => c.Weight * c.Distribution.Mean);

    public override double Sample(DeterministicRng rng)
    {
        double u = rng.NextDouble();
        double acc = 0;
        foreach (var c in Components)
        {
            acc += c.Weight;
            if (u <= acc) return c.Distribution.Sample(rng);
        }
        return Components[^1].Distribution.Sample(rng);
    }

    public double ProbabilityOfOldTail(double thresholdAge, int mcSamples = 200_000)
    {
        long count = 0;
        var rng = new DeterministicRng(0xC0FFEEUL);
        for (int i = 0; i < mcSamples; i++) if (Sample(rng) > thresholdAge) count++;
        return (double)count / mcSamples;
    }

    public double Probability(Func<double, bool> predicate, ulong seed = 0xBADF00D, int mcSamples = 60_000)
    {
        long count = 0;
        var rng = new DeterministicRng(seed);
        for (int i = 0; i < mcSamples; i++) if (predicate(Sample(rng))) count++;
        return (double)count / mcSamples;
    }

    public override double Quantile(double p)
    {
        _sortedSamples ??= BuildSamples();
        var arr = _sortedSamples;
        double idx = p * (arr.Length - 1);
        int i0 = (int)Math.Floor(idx);
        if (i0 >= arr.Length - 1) return arr[^1];
        double f = idx - i0;
        return arr[i0] * (1 - f) + arr[i0 + 1] * f;
    }

    private double[] BuildSamples()
    {
        var rng = new DeterministicRng(0xF157UL);
        int n = 200_000;
        var arr = new double[n];
        for (int i = 0; i < n; i++) arr[i] = Sample(rng);
        Array.Sort(arr);
        return arr;
    }

    public override string Describe() =>
        "mixt(" + string.Join(", ", Components.Select(c => $"{Quantities.UnitCatalog.Format(c.Weight)}*{c.Distribution.Describe()}")) + ")";
}

public sealed record MixtureComponent(double Weight, AgeDistribution Distribution);
