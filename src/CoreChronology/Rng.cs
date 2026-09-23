namespace CoreChronology;

/// <summary>Deterministic PCG32 stream so every replay produces identical draws.</summary>
public sealed class DeterministicRng
{
    private ulong _state;
    private const ulong Multiplier = 6364136223846793005UL;

    public DeterministicRng(ulong seed) => _state = (seed == 0 ? 0x9E3779B97F4A7C15UL : seed);

    public uint NextUInt32()
    {
        ulong old = _state;
        _state = unchecked(old * Multiplier + 1442695040888963407UL);
        uint xorshifted = (uint)(((old >> 18) ^ old) >> 27);
        int rot = (int)(old >> 59);
        return (xorshifted >> rot) | (xorshifted << ((-rot) & 31));
    }

    /// <summary>Uniform in (0,1), 53-bit resolution, never exactly 0 or 1.</summary>
    public double NextDouble() => ((NextUInt32() >> 5) * 67108864.0 + (NextUInt32() >> 6)) * (1.0 / 9007199254740992.0);

    public static ulong StableHash(string text)
    {
        // FNV-1a 64
        ulong hash = 14695981039346656037UL;
        foreach (char c in text)
        {
            hash ^= c;
            hash *= 1099511628211UL;
        }
        return hash;
    }

    public static DeterministicRng ForStream(params string[] streamParts) =>
        new(StableHash(string.Join("|", streamParts)) ^ 0xD1B54A32D192ED03UL);
}
