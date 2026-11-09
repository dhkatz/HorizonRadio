using System;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;

namespace HorizonRadio.Core.IO;

/// <summary>
/// A setting that's a credential (a <c>TextField</c> with <c>IsSecret</c>, like a Spotify Soloist
/// key), encrypted for storing in a plain settings file: Windows DPAPI, bound to the current Windows
/// user, so a copied <c>sources.json</c> doesn't carry it to another user or machine. Stored as
/// <c>dpapi:&lt;base64&gt;</c>. Off Windows the value is kept as written.
/// </summary>
public static class SecretText
{
    public const string Prefix = "dpapi:";

    private static readonly byte[] Entropy = Encoding.UTF8.GetBytes("HorizonRadio.SecretSetting.v1");

    public static bool IsProtected(string? stored) =>
        stored != null && stored.StartsWith(Prefix, StringComparison.Ordinal);

    public static string Protect(string plain)
    {
        ArgumentNullException.ThrowIfNull(plain);
        if (!OperatingSystem.IsWindows()) return plain;
        var cipher = ProtectedData.Protect(Encoding.UTF8.GetBytes(plain), Entropy, DataProtectionScope.CurrentUser);
        return Prefix + Convert.ToBase64String(cipher);
    }

    /// <summary>The plain value of a stored one, or null when it can't be read (another user's,
    /// another machine's, or damaged).</summary>
    public static string? Unprotect(string stored)
    {
        ArgumentNullException.ThrowIfNull(stored);
        if (!IsProtected(stored)) return stored;
        if (!OperatingSystem.IsWindows()) return null;
        try
        {
            var plain = ProtectedData.Unprotect(Convert.FromBase64String(stored[Prefix.Length..]), Entropy,
                DataProtectionScope.CurrentUser);
            return Encoding.UTF8.GetString(plain);
        }
        catch (Exception ex) when (ex is CryptographicException or FormatException)
        {
            Trace.WriteLine($"[hzn-cfg] a secret setting couldn't be decrypted: {ex.Message}");
            return null;
        }
    }
}
