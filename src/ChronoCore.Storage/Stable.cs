using System.Security.Cryptography;
using System.Text;

namespace ChronoCore.Storage;

/// <summary>Deterministic short identity from canonical content. Equivalent
/// inputs always produce the same id and therefore the same ordering.</summary>
public static class Stable
{
    public static string Short(string content)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(content));
        return Convert.ToHexString(bytes)[..10];
    }
}
