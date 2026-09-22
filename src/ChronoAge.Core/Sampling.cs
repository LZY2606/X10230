namespace ChronoAge.Core;

/// <summary>
/// Deterministic sampling of anchor age distributions. A distribution is the evidence: the
/// engine samples it, propagates the full shape (including log-normal long tails), and never
/// substitutes a scalar mean. A per-anchor seed keeps replay bit-for-bit reproducible.
/// </summary>
public static class DistributionSampler
{
    public static double[] Sample(AgeDistributionSpec spec, int n, long seed)
    {
        var rng = new DeterministicRng(seed);
        var x = new double[n];
        switch (spec)
        {
            case NormalSpec norm:
                for (var i = 0; i < n; i++) x[i] = norm.Mean + norm.Sigma * rng.NextGaussian();
                break;
            case LogNormalSpec ln:
                for (var i = 0; i < n; i++)
                {
                    var v = Math.Exp(ln.MuLog + ln.SigmaLog * rng.NextGaussian());
                    x[i] = v;
                }
                break;
            case UniformSpec uni:
                for (var i = 0; i < n; i++) x[i] = uni.Min + rng.NextDouble() * (uni.Max - uni.Min);
                break;
            case EmpiricalSpec emp:
                for (var i = 0; i < n; i++) x[i] = emp.Samples[rng.NextInt(emp.Samples.Count)];
                break;
        }
        return x;
    }

    public static bool TryQuantiles(double[] samples, double[] qs, out double[] outQs)
    {
        outQs = new double[qs.Length];
        if (samples.Length == 0) return false;
        var sorted = samples.OrderBy(v => v).ToArray();
        for (var i = 0; i < qs.Length; i++)
        {
            var q = Math.Clamp(qs[i], 0, 1);
            var pos = q * (sorted.Length - 1);
            var lo = (int)Math.Floor(pos);
            var hi = (int)Math.Ceiling(pos);
            outQs[i] = lo == hi ? sorted[lo] : sorted[lo] + (sorted[hi] - sorted[lo]) * (pos - lo);
        }
        return true;
    }
}

/// <summary>Box-Muller over a deterministic splitmix64 stream — same inputs, same samples.</summary>
public sealed class DeterministicRng
{
    private ulong _state;
    private double? _spare;

    public DeterministicRng(long seed) => _state = (ulong)seed + 0x9E3779B97F4A7C15UL;

    public double NextDouble()
    {
        _state += 0x9E3779B97F4A7C15UL;
        var z = _state;
        z = (z ^ (z >> 30)) * 0xBF58476D1CE4E5B9UL;
        z = (z ^ (z >> 27)) * 0x94D049BB133111EBUL;
        z ^= z >> 31;
        return (z >> 11) * (1.0 / 9007199254740992.0);
    }

    public int NextInt(int maxExclusive) => (int)(NextDouble() * maxExclusive);

    public double NextGaussian()
    {
        if (_spare is double s) { _spare = null; return s; }
        double u, v, s2;
        do { u = 2.0 * NextDouble() - 1.0; v = 2.0 * NextDouble() - 1.0; s2 = u * u + v * v; }
        while (s2 is <= 1e-12 or >= 1.0);
        var m = Math.Sqrt(-2.0 * Math.Log(s2) / s2);
        _spare = v * m;
        return u * m;
    }
}
