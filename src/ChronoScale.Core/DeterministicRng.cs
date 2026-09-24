namespace ChronoScale.Core;

/// <summary>Deterministic xorshift64* RNG so derived quantiles are identical on every machine and replay.</summary>
public sealed class DeterministicRng
{
    private ulong _state;
    public DeterministicRng(ulong seed) => _state = seed == 0 ? 0x9E3779B97F4A7C15UL : seed;

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

    public double StandardNormal()
    {
        double u1 = 1.0 - NextDouble();
        double u2 = NextDouble();
        return Math.Sqrt(-2.0 * Math.Log(u1)) * Math.Sin(2.0 * Math.PI * u2);
    }
}
