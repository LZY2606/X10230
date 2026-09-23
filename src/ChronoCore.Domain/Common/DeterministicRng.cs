using System;

namespace ChronoCore.Domain.Common;

/// <summary>
/// SplitMix64: fixed-seed, fully deterministic pseudo random source. Sampling used to
/// build uncertainty bands derives from this RNG keyed by a stable candidate fingerprint,
/// so replay and export/import reproduce every draw.
/// </summary>
public sealed class DeterministicRng
{
    private ulong _state;

    public DeterministicRng(ulong seed)
    {
        _state = seed;
    }

    public ulong NextUInt64()
    {
        _state += 0x9E3779B97F4A7C15UL;
        var z = _state;
        z = (z ^ (z >> 30)) * 0xBF58476D1CE4E5B9UL;
        z = (z ^ (z >> 27)) * 0x94D049BB133111EBUL;
        return z ^ (z >> 31);
    }

    /// <summary>Uniform double in [0,1).</summary>
    public double NextDouble() => (NextUInt64() >> 11) * (1.0 / 9007199254740992.0);
}
