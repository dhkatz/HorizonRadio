using System.Security.Cryptography;
using System.Text;

namespace HorizonRadio.Sdk.Metadata;

/// <summary>The key a provider files a lookup under in the <see cref="IMetadataCache"/>.</summary>
public static class MetadataCacheKey
{
    /// <summary>A stable key for <paramref name="providerId"/>'s <paramref name="query"/>: a hash,
    /// safe as a file name. Normalize the query first (lowercase it) so one song finds one entry.</summary>
    public static string For(string providerId, string query)
    {
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes($"{providerId}:{query}"));
        // A 16-byte prefix is plenty to keep entries apart, and keeps file names short.
        return Convert.ToHexString(hash, 0, 16);
    }
}
