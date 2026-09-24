using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace CoreChrono.Engine;

public static class Support
{
    public static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = false,
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull
    };

    public static string Sha256(string s)
        => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(s)));

    public static string CanonicalHash(object? o) => Sha256(JsonSerializer.Serialize(o, Json));

    public static double Quantile(double[] sorted, double p)
    {
        if (sorted.Length == 0) return double.NaN;
        if (sorted.Length == 1) return sorted[0];
        var pos = p * (sorted.Length - 1);
        var lo = (int)Math.Floor(pos);
        var hi = (int)Math.Ceiling(pos);
        return sorted[lo] + (sorted[hi] - sorted[lo]) * (pos - lo);
    }
}

public sealed class DeterministicRng
{
    private ulong _state;
    public DeterministicRng(string seedText)
    {
        var bytes = System.Security.Cryptography.SHA256.HashData(Encoding.UTF8.GetBytes(seedText));
        _state = BitConverter.ToUInt64(bytes, 0) | 1UL;
    }

    public double NextDouble()
    {
        // xorshift64*
        ulong x = _state;
        x ^= x >> 12; x ^= x << 25; x ^= x >> 27;
        _state = x;
        return ((x * 0x2545F4914F6CDD1DUL) >> 11) / (double)(1UL << 53);
    }

    public double Normal(double mean, double sd)
    {
        double u1 = 1.0 - NextDouble();
        double u2 = 1.0 - NextDouble();
        double z = Math.Sqrt(-2.0 * Math.Log(u1)) * Math.Cos(2.0 * Math.PI * u2);
        return mean + z * sd;
    }
}
