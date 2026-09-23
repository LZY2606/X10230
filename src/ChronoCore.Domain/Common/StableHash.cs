using System;
using System.Globalization;
using System.Text;

namespace ChronoCore.Domain.Common;

/// <summary>
/// Deterministic, allocation-light FNV-1a 64-bit hash used for stable identities,
/// canonical fingerprints and deterministic sampling seeds. Never use System.Random
/// with time based seeds for derived results: equivalent inputs must always compare
/// equal across machines, runs and export/import round trips.
/// </summary>
public static class StableHash
{
    public const ulong Fnv64Offset = 14695981039346656037UL;
    public const ulong Fnv64Prime = 1099511628211UL;

    public static ulong Update(ulong hash, byte b)
    {
        hash ^= b;
        return hash * Fnv64Prime;
    }

    public static ulong OfString(string? value, ulong seed = Fnv64Offset)
    {
        var hash = seed;
        if (value is null)
        {
            hash = Update(hash, 0);
            return hash;
        }

        var bytes = Encoding.UTF8.GetBytes(value);
        foreach (var b in bytes)
        {
            hash = Update(hash, b);
        }

        return hash;
    }

    public static ulong OfStrings(params string?[] parts)
    {
        var hash = Fnv64Offset;
        foreach (var p in parts)
        {
            hash = OfString(p, hash);
            // separator prevents ("ab","c") collapsing into ("a","bc")
            hash = Update(hash, 0x1F);
        }

        return hash;
    }

    public static string Short(ulong hash) => hash.ToString("x12", CultureInfo.InvariantCulture);
}
