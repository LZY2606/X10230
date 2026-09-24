namespace DepthAge.Core;

/// <summary>
/// 平台无关的确定性伪随机源（xorshift64* 变体 + FNV-1a 播种）。
/// 相同输入（材料身份 + 参数版本）永远产生相同抽样顺序，保证等价输入得到稳定候选顺序。
/// </summary>
public sealed class XorShift
{
    private ulong _state;

    public XorShift(ulong seed) => _state = seed == 0 ? 0x9E3779B97F4A7C15UL : seed;

    public uint NextUInt32()
    {
        var x = _state;
        x ^= x << 13;
        x ^= x >> 7;
        x ^= x << 17;
        _state = x;
        return (uint)(x >> 32);
    }

    public double NextDouble() => (NextUInt32() + 0.5) * (1.0 / 4294967296.0);

    public static ulong SeedFrom(string text)
    {
        ulong hash = 1469598103934665603UL;
        foreach (var ch in text)
        {
            hash ^= ch;
            hash *= 1099511628211UL;
        }
        return hash;
    }
}
