using System;
using System.Linq;
using System.Text;
using HorizonRadio.Sdk;

namespace HorizonRadio.Core.IO;

/// <summary>
/// A setting that's a credential (a <c>TextField</c> with <c>IsSecret</c>, like a Spotify Soloist
/// key), protected for storing in a plain settings file by the host's <see cref="ISecretProtector"/>
/// (DPAPI on Windows, bound to the current Windows user), so a copied <c>settings.json</c> doesn't
/// carry it to another user or machine. Stored as <c>&lt;scheme&gt;:&lt;base64&gt;</c>
/// (<c>dpapi:…</c>). With nothing to protect with, the value is kept as written.
/// </summary>
public sealed class SecretText(ISecretProtector protector)
{
    // The schemes any platform stores secrets with, so a secret this one can't read (copied from
    // another OS) is still known to be one and kept. A platform with a new scheme adds it here.
    private static readonly string[] Schemes = ["dpapi"];

    private const string Purpose = "HorizonRadio.SecretSetting.v1";

    /// <summary>Whether <paramref name="stored"/> is a protected secret (of any scheme).</summary>
    public static bool IsProtected(string? stored) =>
        stored != null && Schemes.Any(s => stored.StartsWith(s + ":", StringComparison.Ordinal));

    /// <exception cref="System.Security.Cryptography.CryptographicException">The OS couldn't protect it.</exception>
    public string Protect(string plain)
    {
        ArgumentNullException.ThrowIfNull(plain);
        if (protector.Scheme is not { } scheme) return plain;
        return $"{scheme}:{Convert.ToBase64String(protector.Protect(Encoding.UTF8.GetBytes(plain), Purpose))}";
    }

    /// <summary>The plain value of a stored one, or null when it can't be read (another user's,
    /// another machine's or another OS's, or damaged).</summary>
    public string? Unprotect(string stored)
    {
        ArgumentNullException.ThrowIfNull(stored);
        if (!IsProtected(stored)) return stored;
        if (protector.Scheme is not { } scheme || !stored.StartsWith(scheme + ":", StringComparison.Ordinal)) return null;
        byte[] data;
        try { data = Convert.FromBase64String(stored[(scheme.Length + 1)..]); }
        catch (FormatException) { return null; }
        return protector.Unprotect(data, Purpose) is { } plain ? Encoding.UTF8.GetString(plain) : null;
    }
}
