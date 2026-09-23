using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace AgeDepth.Core;

public static class Determinism
{
    /// <summary>Canonical JSON: object keys sorted, invariant formatting.</summary>
    public static string Canonical(JsonNode? node)
    {
        var sb = new StringBuilder();
        Write(node, sb);
        return sb.ToString();
    }

    private static void Write(JsonNode? node, StringBuilder sb)
    {
        switch (node)
        {
            case null:
                sb.Append("null");
                break;
            case JsonValue v:
                sb.Append(v.ToJsonString());
                break;
            case JsonArray a:
                sb.Append('[');
                for (var i = 0; i < a.Count; i++)
                {
                    if (i > 0) sb.Append(',');
                    Write(a[i], sb);
                }
                sb.Append(']');
                break;
            case JsonObject o:
                sb.Append('{');
                var first = true;
                foreach (var kv in o.OrderBy(k => k.Key, StringComparer.Ordinal))
                {
                    if (!first) sb.Append(',');
                    first = false;
                    sb.Append(JsonSerializer.Serialize(kv.Key));
                    sb.Append(':');
                    Write(kv.Value, sb);
                }
                sb.Append('}');
                break;
        }
    }

    public static string Sha256Hex(string text)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(text));
        return Convert.ToHexString(bytes).ToLowerInvariant();
    }

    /// <summary>Stable, culture-invariant numeric formatting.</summary>
    public static string Num(double v) =>
        v.ToString("G17", System.Globalization.CultureInfo.InvariantCulture);
}

/// <summary>Deterministic xorshift64* stream; identical seeds give identical draws on every run.</summary>
public struct XorShift
{
    private ulong _s;

    public XorShift(ulong seed)
    {
        _s = seed == 0 ? 0x9E3779B97F4A7C15UL : seed;
    }

    public ulong NextU64()
    {
        var x = _s;
        x ^= x >> 12;
        x ^= x << 25;
        x ^= x >> 27;
        _s = x;
        return x * 0x2545F4914F6CDD1DUL;
    }

    /// <summary>Uniform in [0,1).</summary>
    public double NextDouble() => (NextU64() >> 11) * (1.0 / 9007199254740992.0);

    public int NextInt(int exclusiveMax) => (int)(NextU64() % (ulong)exclusiveMax);
}
