using System.Security.Cryptography;
using System.Text;

namespace AgeDepth.Core;

/// <summary>
/// Deterministic identity/order/randomness helpers. Identical inputs always yield identical
/// digests, candidate order and Monte Carlo draws, so replay and export/import round-trips
/// are stable.
/// </summary>
public static class Determinism
{
    public static string Sha8(string text)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(text));
        return Convert.ToHexString(bytes[..4]).ToLowerInvariant();
    }

    /// <summary>Stable ordering key for equivalent records (id, then numeric depth, then raw text).</summary>
    public static (string, double, string) OrderKey(string id, double depth, string raw) =>
        (id, depth, raw);

    /// <summary>A reproducible PRNG so uncertainty bands are identical on replay.</summary>
    public static Random SeededRandom(string seed)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(seed));
        var seedInt = BitConverter.ToInt32(bytes, 0);
        return new Random(unchecked(seedInt));
    }

    /// <summary>Inverse-normal via Beasley-Springer-Moro, deterministic and no special-case branch bug.</summary>
    public static double InverseNormal(double u)
    {
        u = Math.Clamp(u, 1e-12, 1 - 1e-12);
        var a = new[]
        {
            -3.969683028665376e+01, 2.209460984245205e+02,
            -2.759285104469687e+02, 1.383577518672690e+02,
            -3.066479806614716e+01, 2.506628277459239e+00
        };
        var b = new[]
        {
            -5.447609879822406e+01, 1.615858368580409e+02,
            -1.556989798598866e+02, 6.680131188771972e+01,
            -1.328068155288572e+01
        };
        var c = new[]
        {
            -7.784894002430293e-03, -3.223964580411365e-01,
            -2.400758277161838e+00, -2.549732539343734e+00,
            4.374664141464968e+00, 2.938163982698783e+00
        };
        var d = new[]
        {
            7.784695709041462e-03, 3.224671290700398e-01,
            2.445134137142996e+00, 3.754408661907416e+00
        };

        const double plow = 0.02425, phigh = 1 - plow;
        double q, r;
        if (u < plow)
        {
            q = Math.Sqrt(-2 * Math.Log(u));
            return (((((c[0] * q + c[1]) * q + c[2]) * q + c[3]) * q + c[4]) * q + c[5]) /
                   ((((d[0] * q + d[1]) * q + d[2]) * q + d[3]) * q + 1);
        }
        if (u <= phigh)
        {
            q = u - 0.5; r = q * q;
            return (((((a[0] * r + a[1]) * r + a[2]) * r + a[3]) * r + a[4]) * r + a[5]) * q /
                   (((((b[0] * r + b[1]) * r + b[2]) * r + b[3]) * r + b[4]) * r + 1);
        }
        q = Math.Sqrt(-2 * Math.Log(1 - u));
        return -(((((c[0] * q + c[1]) * q + c[2]) * q + c[3]) * q + c[4]) * q + c[5]) /
                ((((d[0] * q + d[1]) * q + d[2]) * q + d[3]) * q + 1);
    }
}
