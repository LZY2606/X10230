using System;
using System.Collections.Generic;
using System.Linq;

namespace Chrono.Core.Quantities;

/// <summary>
/// A distribution over calendar age. Anchors carry a full distribution — never reduced to a mean.
/// Sample draws are deterministic for a seed so equivalent inputs replay identically and order stably.
/// </summary>
public interface IAgeDistribution
{
    string Kind { get; }
    string Datum { get; }
    double Draw(DeterministicRng rng);
    /// <summary>Parameter summary used for input fingerprints; values carry the cal yr BP datum.</summary>
    IReadOnlyList<double> Parameters { get; }
}

/// <summary>Deterministic SplitMix64-style PRNG. Same (seed, sequence) always yields the same draw.</summary>
public sealed class DeterministicRng
{
    private ulong _state;
    public DeterministicRng(ulong seed) => _state = seed;

    public ulong NextUInt64()
    {
        _state += 0x9E3779B97F4A7C15UL;
        var z = _state;
        z = (z ^ (z >> 30)) * 0xBF58476D1CE4E5B9UL;
        z = (z ^ (z >> 27)) * 0x94D049BB133111EBUL;
        return z ^ (z >> 31);
    }

    public double NextDouble() => (NextUInt64() >> 11) * (1.0 / 9007199254740992.0);
}

public sealed record NormalAgeDistribution(double Mean, double Sigma, string Datum) : IAgeDistribution
{
    public string Kind => "normal";
    public IReadOnlyList<double> Parameters => new[] { Mean, Sigma };
    public double Draw(DeterministicRng rng)
    {
        // Box-Muller
        double u1 = 1.0 - rng.NextDouble();
        double u2 = rng.NextDouble();
        return Mean + Sigma * Math.Sqrt(-2.0 * Math.Log(u1)) * Math.Cos(2.0 * Math.PI * u2);
    }
}

public sealed record StudentTAgeDistribution(double Location, double Scale, double DegreesFreedom, string Datum) : IAgeDistribution
{
    public string Kind => "student_t";
    public IReadOnlyList<double> Parameters => new[] { Location, Scale, DegreesFreedom };
    public double Draw(DeterministicRng rng)
    {
        double z = Normal(rng);
        double g = Gamma(rng, DegreesFreedom / 2.0);
        return Location + Scale * z / Math.Sqrt(2.0 * g / DegreesFreedom);
    }

    internal static double Normal(DeterministicRng rng)
    {
        double u1 = 1.0 - rng.NextDouble();
        double u2 = rng.NextDouble();
        return Math.Sqrt(-2.0 * Math.Log(u1)) * Math.Cos(2.0 * Math.PI * u2);
    }

    // Marsaglia-Tsang gamma, valid shape >= 1; for shape < 1 use the boost k<1 identity.
    internal static double Gamma(DeterministicRng rng, double shape)
    {
        if (shape >= 1.0) return GammaMarsagliaTsang(rng, shape);
        return GammaMarsagliaTsang(rng, shape + 1.0) * Math.Pow(rng.NextDouble(), 1.0 / shape);
    }

    private static double GammaMarsagliaTsang(DeterministicRng rng, double shape)
    {
        double d = shape - 1.0 / 3.0;
        double c = 1.0 / Math.Sqrt(9.0 * d);
        while (true)
        {
            double x, v;
            do
            {
                x = Normal(rng);
                v = 1.0 + c * x;
            } while (v <= 0);
            v = v * v * v;
            double u = rng.NextDouble();
            if (u < 1.0 - 0.0331 * (x * x) * (x * x)) return d * v;
            if (Math.Log(u) < 0.5 * x * x + d * (1.0 - v + Math.Log(v))) return d * v;
        }
    }
}

/// <summary>Mixture distribution — the long-tail case. Components must stay distinct, not collapsed.</summary>
public sealed record MixtureAgeDistribution(IReadOnlyList<MixtureComponent> Components, string Datum) : IAgeDistribution
{
    public string Kind => "mixture";
    public IReadOnlyList<double> Parameters => Components.SelectMany(c => new[] { c.Weight, c.Distribution.Parameters[0] }).ToArray();

    public double Draw(DeterministicRng rng)
    {
        var pick = rng.NextDouble();
        double acc = 0;
        foreach (var c in Components)
        {
            acc += c.Weight;
            if (pick <= acc) return c.Distribution.Draw(rng);
        }
        return Components[^1].Distribution.Draw(rng);
    }
}

public sealed record MixtureComponent(double Weight, IAgeDistribution Distribution);

/// <summary>A reservoir (marine/lake) offset: another distribution subtracted from the measured age.</summary>
public sealed record ReservoirOffset(string Id, string? AppliesToCore, IAgeDistribution Offset, bool Enabled);

public static class DistributionQuantiles
{
    public static double Quantile(IReadOnlyList<double> sortedAsc, double q)
    {
        if (sortedAsc.Count == 0) throw new InvalidOperationException("no samples");
        if (sortedAsc.Count == 1) return sortedAsc[0];
        double pos = q * (sortedAsc.Count - 1);
        int lo = (int)Math.Floor(pos);
        int hi = Math.Min(lo + 1, sortedAsc.Count - 1);
        double frac = pos - lo;
        return sortedAsc[lo] * (1 - frac) + sortedAsc[hi] * frac;
    }

    public static double[] Band(IReadOnlyList<double> sortedAsc) => new[]
    {
        Quantile(sortedAsc, 0.025), Quantile(sortedAsc, 0.50), Quantile(sortedAsc, 0.975)
    };
}
