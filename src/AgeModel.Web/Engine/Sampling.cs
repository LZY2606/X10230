using System.Security.Cryptography;
using System.Text;
using AgeModel.Domain;

namespace AgeModel.Engine;

/// <summary>确定性分层采样：同一候选在各深度使用同一组分层分位，得到相关（联合）不确定度带。</summary>
public static class Sampling
{
    public static double[] StratifiedQuantiles(int draws, string seedKey)
    {
        var u = new double[draws];
        // 确定性抖动，种子来自稳定身份，保证等价输入同序、同值。
        int seed = StableHash(seedKey);
        var rng = new DeterministicRng((uint)seed);
        for (int i = 0; i < draws; i++)
        {
            double jitter = rng.NextDouble();
            u[i] = (i + jitter) / draws;
            u[i] = Math.Clamp(u[i], 1e-7, 1 - 1e-7);
        }
        Array.Sort(u);
        return u;
    }

    public static double[] Draw(this AgeDistribution dist, double[] us)
    {
        var v = new double[us.Length];
        for (int i = 0; i < us.Length; i++) v[i] = dist.Quantile(us[i]);
        return v;
    }

    public static bool IsUsable(this AgeDistribution dist) =>
        dist is not UnconvertedDist && double.IsFinite(dist.Quantile(0.5));

    /// <summary>储层偏移校正：校正年龄 = 测量年龄 − ΔR；以分布卷积实现，不用均值替代。</summary>
    public static AgeDistribution CorrectReservoir(AgeDistribution measured, ReservoirOffset? r, int draws, string seedKey)
    {
        if (r is null) return measured;
        var us = StratifiedQuantiles(draws, seedKey + ":res");
        var m = measured.Draw(us);
        var off = r.Distribution.Draw(us);
        var corrected = new double[draws];
        for (int i = 0; i < draws; i++) corrected[i] = m[i] - off[i];
        return new SampleDist(corrected);
    }

    public static double MonotonicityProbability(double[] olderDraws, double[] youngerDraws)
    {
        int win = 0, n = Math.Min(olderDraws.Length, youngerDraws.Length);
        for (int i = 0; i < n; i++) if (olderDraws[i] >= youngerDraws[i]) win++;
        return win / (double)n;
    }

    public static int StableHash(string s)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(s));
        return BitConverter.ToInt32(bytes, 0) & 0x7fffffff;
    }

    public static string StableDigest(string canonical)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(canonical));
        return Convert.ToHexString(bytes).ToLowerInvariant();
    }
}

/// <summary>xorshift32：跨平台、跨进程确定性。</summary>
public sealed class DeterministicRng
{
    private uint _x;
    public DeterministicRng(uint seed) => _x = seed == 0 ? 0x9e3779b9u : seed;
    public double NextDouble()
    {
        _x ^= _x << 13; _x ^= _x >> 17; _x ^= _x << 5;
        return (_x & 0xFFFFFF) / (double)0x1000000;
    }
}
