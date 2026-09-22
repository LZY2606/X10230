namespace AgeModel.Core.Distributions;

/// <summary>Deterministic xoshiro128** RNG so Monte-Carlo runs are reproducible per seed.</summary>
public sealed class DeterministicRng
{
    private uint _s0, _s1, _s2, _s3;

    public DeterministicRng(ulong seed)
    {
        // splitmix64 to fill state
        ulong z = seed;
        _s0 = (uint)Next(ref z); _s1 = (uint)(Next(ref z) >> 32);
        _s2 = (uint)Next(ref z); _s3 = (uint)(Next(ref z) >> 32);
        if (_s0 == 0 && _s1 == 0 && _s2 == 0 && _s3 == 0) _s3 = 1;
    }

    private static ulong Next(ref ulong z)
    {
        z += 0x9E3779B97F4A7C15UL;
        ulong x = z;
        x = (x ^ (x >> 30)) * 0xBF58476D1CE4E5B9UL;
        x = (x ^ (x >> 27)) * 0x94D049BB133111EBUL;
        return x ^ (x >> 31);
    }

    private uint NextUInt()
    {
        uint result = Rotl(_s1 * 5, 7) * 9;
        uint t = _s1 << 9;
        _s2 ^= _s0; _s3 ^= _s1; _s1 ^= _s2; _s0 ^= _s3; _s2 ^= t;
        _s3 = Rotl(_s3, 11);
        return result;
    }

    private static uint Rotl(uint x, int k) => (x << k) | (x >> (32 - k));

    public double NextDouble() => (NextUInt() >> 8) * (1.0 / 16777216.0);

    public double NextNormal()
    {
        // Box-Muller
        double u1 = Math.Max(NextDouble(), 1e-12);
        double u2 = NextDouble();
        return Math.Sqrt(-2.0 * Math.Log(u1)) * Math.Cos(2.0 * Math.PI * u2);
    }

    public int NextInt(int exclusiveMax) => (int)(NextUInt() % (uint)exclusiveMax);
}
