using System.Security.Cryptography;
using System.Text;

namespace Chrono.Core;

/// <summary>Deterministic, seedable RNG so equivalent inputs reproduce identical draws and ordering.</summary>
public sealed class DeterministicRng
{
    private ulong _s;
    public DeterministicRng(ulong seed) => _s = seed == 0 ? 0x9E3779B97F4A7C15UL : seed;

    public ulong NextU64()
    {
        // splitmix64
        _s += 0x9E3779B97F4A7C15UL;
        var z = _s;
        z = (z ^ (z >> 30)) * 0xBF58476D1CE4E5B9UL;
        z = (z ^ (z >> 27)) * 0x94D049BB133111EBUL;
        return z ^ (z >> 31);
    }

    public double NextDouble() => (NextU64() >> 11) * (1.0 / 9007199254740992.0);
    public double Normal()
    {
        // Box-Muller
        var u1 = 1.0 - NextDouble();
        var u2 = NextDouble();
        return Math.Sqrt(-2.0 * Math.Log(u1)) * Math.Cos(2.0 * Math.PI * u2);
    }

    /// <summary>Skew-normal via the two-normal construction (alpha implied by skew in (-1,1)).</summary>
    public double SkewNormal(double mu, double sigma, double skew)
    {
        var u0 = Normal();
        var v = Normal();
        var delta = skew / Math.Sqrt(1.0 + skew * skew);
        var z = delta * Math.Abs(u0) + Math.Sqrt(1.0 - delta * delta) * v;
        return mu + sigma * z;
    }
}

public static class Hashing
{
    public static string Sha256Hex(string s) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(s))).ToLowerInvariant();
}
