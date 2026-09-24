using System.Security.Cryptography;
using System.Text;

namespace AgeDepth.Core;

public static class HashUtil
{
    public static string Sha256(string input)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(input));
        return Convert.ToHexString(bytes).ToLowerInvariant();
    }

    public static string ShortHash(string input, int len = 12)
        => Sha256(input)[..len];
}
