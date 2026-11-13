using System.Diagnostics;
using System.Text;
using HorizonRadio.Sdk;
using HorizonRadio.Sdk.IO;

namespace HorizonRadio.Source.Spotify;

/// <summary>
/// Stores the Spotify OAuth refresh token, protected at rest by the host's
/// <see cref="ISecretProtector"/> (DPAPI for the current Windows user). A refresh token is an
/// account credential, so it's bound to this user on this machine (it can't be copied to
/// another).
///
/// Lives at <c>spotify-auth.dat</c> in the app's data folder. All operations are best-effort;
/// with nothing to protect it with (an OS without platform code for it) or on any failure they
/// degrade to "no token", so the user signs in each session, rather than keeping the token in
/// the clear or throwing.
/// </summary>
public sealed class SpotifyAuthStore
{
    // Namespaces the protected bytes, so they aren't interchangeable with any other secret.
    private const string Purpose = "HorizonRadio.Spotify.RefreshToken.v1";

    private readonly string _path;
    private readonly ISecretProtector _secrets;

    /// <param name="path">The file it keeps the token in (the plugin's: <c>spotify-auth.dat</c> in the
    /// app's data folder).</param>
    /// <param name="secrets">What it protects the token with (the host's).</param>
    public SpotifyAuthStore(string path, ISecretProtector secrets)
    {
        _path = path;
        _secrets = secrets;
    }

    private static void Log(string msg) => Trace.WriteLine($"[hzn-spotify-auth] {msg}");

    /// <summary>The stored refresh token, or null if none / unreadable.</summary>
    public string? Load()
    {
        if (_secrets.Scheme == null || !File.Exists(_path)) return null;
        try
        {
            return _secrets.Unprotect(File.ReadAllBytes(_path), Purpose) is { } plain ? Encoding.UTF8.GetString(plain) : null;
        }
        catch (Exception ex)
        {
            Log($"load failed: {ex.Message}");
            return null;
        }
    }

    public void Save(string refreshToken)
    {
        if (_secrets.Scheme == null || string.IsNullOrEmpty(refreshToken)) return;
        try
        {
            AtomicFile.WriteAllBytes(_path, _secrets.Protect(Encoding.UTF8.GetBytes(refreshToken), Purpose));
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
}
