using System.Text;

namespace ChronoCore;

/// <summary>Deterministic RNG (xorshift64*) so equivalent inputs always give identical results.</summary>
public sealed class DeterministicRng
{
    private ulong _state;

    public DeterministicRng(int seed) => _state = seed == 0 ? 0x9E3779B97F4A7C15UL : (ulong)seed;

    public ulong NextUlong()
    {
        ulong x = _state;
        x ^= x >> 12;
        x ^= x << 25;
        x ^= x >> 27;
        _state = x;
        return x * 0x2545F4914F6CDD1DUL;
    }

    public double NextDouble() => (NextUlong() >> 11) * (1.0 / 9007199254740992.0);

    public double NextNormal()
    {
        double u1 = 1.0 - NextDouble();
        double u2 = 1.0 - NextDouble();
        return Math.Sqrt(-2.0 * Math.Log(u1)) * Math.Cos(2.0 * Math.PI * u2);
    }

    // Marsaglia & Tsang gamma sampler (k >= 1); for k < 1 use Ahrens–Dieter boost.
    public double NextGamma(double shape, double scale)
    {
        if (shape < 1.0)
        {
            double p = NextDouble();
            double boosted = NextGamma(shape + 1.0, 1.0);
            double u = Math.Pow(p, 1.0 / shape);
            return boosted * u * scale;
        }
        double d = shape - 1.0 / 3.0;
        double c = 1.0 / Math.Sqrt(9.0 * d);
        while (true)
        {
            double x = NextNormal();
            double v = 1.0 + c * x;
            if (v <= 0) continue;
            v = v * v * v;
            double u = NextDouble();
            if (u < 1.0 - 0.0331 * (x * x) * (x * x)) return d * v * scale;
            if (Math.Log(u) < 0.5 * x * x + d * (1.0 - v + Math.Log(v))) return d * v * scale;
        }
    }
}

public static class Sampling
{
    public static int SeedFor(int baseSeed, string anchorId)
    {
        unchecked
        {
            uint h = (uint)baseSeed;
            foreach (char ch in anchorId) h = h * 131 + ch;
            return (int)(h & 0x7FFFFFFF);
        }
    }

    /// <summary>Draw one effective age sample for an anchor: distribution minus reservoir offset.</summary>
    public static double DrawEffectiveAge(Anchor a, DeterministicRng rng)
    {
        var d = a.Distribution;
        double raw = d.Kind switch
        {
            "empirical" when d.EmpiricalSamples.Count > 0 =>
                d.EmpiricalSamples[(int)(rng.NextDouble() * d.EmpiricalSamples.Count)] + (d.OffsetYearsBp ?? 0),
            "gamma" => rng.NextGamma(Math.Max(d.Shape ?? 1, 0.0001), Math.Max(d.Scale ?? 1, 1e-9))
                       + (d.OffsetYearsBp ?? d.MeanYearsBp ?? 0),
            "lognormal" => Math.Exp((d.MeanYearsBp ?? 0) + (d.Shape ?? 0.2) * rng.NextNormal())
                           + (d.OffsetYearsBp ?? 0),
            _ => (d.MeanYearsBp ?? a.RawAge.YearsBp ?? 0) + (d.StdYearsBp ?? 0) * rng.NextNormal()
        };
        double reservoir = a.Reservoir.MeanYears + a.Reservoir.StdYears * rng.NextNormal();
        return raw - reservoir;
    }

    public static QuantileBand Summarize(IReadOnlyList<double> xs)
    {
        var sorted = xs.ToArray();
        Array.Sort(sorted);
        double q(double p) => sorted[Math.Clamp((int)Math.Round(p * (sorted.Length - 1)), 0, sorted.Length - 1)];
        double mean = sorted.Average();
        double variance = sorted.Sum(x => (x - mean) * (x - mean)) / sorted.Length;
        return new QuantileBand
        {
            P05 = q(0.05), P50 = q(0.50), P95 = q(0.95),
            Mean = mean, Std = Math.Sqrt(variance)
        };
    }

    /// <summary>P(anchorA older than anchorB) by paired sampling. Older = larger years BP.</summary>
    public static double ProbabilityOlder(Anchor a, Anchor b, int draws, int seed)
    {
        var rngA = new DeterministicRng(SeedFor(seed, a.Id + ":cmp"));
        var rngB = new DeterministicRng(SeedFor(seed, b.Id + ":cmp"));
        int older = 0;
        for (int i = 0; i < draws; i++)
            if (DrawEffectiveAge(a, rngA) >= DrawEffectiveAge(b, rngB)) older++;
        return older / (double)draws;
    }

    public static string StableHash(IEnumerable<string> parts)
    {
        var data = Encoding.UTF8.GetBytes(string.Join("\u0001", parts));
        unchecked
        {
            const ulong p = 1099511628211UL;
            ulong h = 14695981039346656037UL;
            foreach (byte b in data) { h ^= b; h *= p; }
            return "cand-" + h.ToString("x16");
        }
    }
}
