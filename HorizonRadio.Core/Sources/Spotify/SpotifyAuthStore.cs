using System;
using System.Diagnostics;
using System.IO;
using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Text;

namespace HorizonRadio.Core.Sources.Spotify;

/// <summary>
/// Stores the Spotify OAuth refresh token, encrypted at rest with Windows DPAPI
/// (per-user). A refresh token is an account credential — more sensitive than the
/// app's config secrets — so unlike the plaintext config stores it's encrypted and
/// bound to the current Windows user (it can't be copied to another machine/user).
///
/// Lives at <c>%LOCALAPPDATA%\HorizonRadio\spotify-auth.dat</c>. All operations are
/// best-effort and Windows-only; on a non-Windows host (or any failure) they degrade
/// to "no token" rather than throwing, so the rest of the app keeps working.
/// </summary>
public sealed class SpotifyAuthStore
{
    // Extra entropy mixed into the DPAPI blob — namespaces our ciphertext so it
    // isn't interchangeable with any other DPAPI data for this user.
    private static readonly byte[] Entropy = Encoding.UTF8.GetBytes("HorizonRadio.Spotify.RefreshToken.v1");

    private readonly string _path;

    public SpotifyAuthStore(string? path = null)
    {
        _path = path ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "HorizonRadio", "spotify-auth.dat");
    }

    private static void Log(string msg) => Debug.WriteLine($"[hzn-spotify-auth] {msg}");

    /// <summary>The stored refresh token, or null if none / unreadable.</summary>
    public string? Load()
    {
        if (!OperatingSystem.IsWindows() || !File.Exists(_path)) return null;
        try
        {
            return Decrypt(File.ReadAllBytes(_path));
        }
        catch (Exception ex)
        {
            Log($"load failed: {ex.Message}");
            return null;
        }
    }

    public void Save(string refreshToken)
    {
        if (!OperatingSystem.IsWindows() || string.IsNullOrEmpty(refreshToken)) return;
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
            File.WriteAllBytes(_path, Encrypt(refreshToken));
        }
        catch (Exception ex)
        {
            Log($"save failed: {ex.Message}");
        }
    }

    public void Clear()
    {
        try { if (File.Exists(_path)) File.Delete(_path); }
        catch (Exception ex) { Log($"clear failed: {ex.Message}"); }
    }

    [SupportedOSPlatform("windows")]
    private static byte[] Encrypt(string value) =>
        ProtectedData.Protect(Encoding.UTF8.GetBytes(value), Entropy, DataProtectionScope.CurrentUser);

    [SupportedOSPlatform("windows")]
    private static string Decrypt(byte[] blob) =>
        Encoding.UTF8.GetString(ProtectedData.Unprotect(blob, Entropy, DataProtectionScope.CurrentUser));
}
