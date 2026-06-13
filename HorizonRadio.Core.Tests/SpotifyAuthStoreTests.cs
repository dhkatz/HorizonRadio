using System;
using HorizonRadio.Core.Sources.Spotify;

namespace HorizonRadio.Core.Tests;

/// <summary>
/// The DPAPI-encrypted refresh-token store round-trips and clears. DPAPI is
/// Windows-only, so these are no-ops (trivially pass) elsewhere.
/// </summary>
public class SpotifyAuthStoreTests
{
    [Fact]
    public void Round_trips_a_token()
    {
        if (!OperatingSystem.IsWindows()) return;
        using var dir = TempDir.Create();
        var path = System.IO.Path.Combine(dir.Path, "spotify-auth.dat");
        var store = new SpotifyAuthStore(path);

        Assert.Null(store.Load());                 // nothing yet

        store.Save("refresh-token-abc123");
        Assert.True(System.IO.File.Exists(path));
        Assert.Equal("refresh-token-abc123", store.Load());

        // A fresh instance over the same path reads it back (decryptable on disk).
        Assert.Equal("refresh-token-abc123", new SpotifyAuthStore(path).Load());
    }

    [Fact]
    public void Stored_blob_is_not_plaintext()
    {
        if (!OperatingSystem.IsWindows()) return;
        using var dir = TempDir.Create();
        var path = System.IO.Path.Combine(dir.Path, "spotify-auth.dat");
        new SpotifyAuthStore(path).Save("super-secret-refresh-token");

        var bytes = System.IO.File.ReadAllBytes(path);
        var asText = System.Text.Encoding.UTF8.GetString(bytes);
        Assert.DoesNotContain("super-secret-refresh-token", asText); // encrypted at rest
    }

    [Fact]
    public void Clear_removes_the_token()
    {
        if (!OperatingSystem.IsWindows()) return;
        using var dir = TempDir.Create();
        var path = System.IO.Path.Combine(dir.Path, "spotify-auth.dat");
        var store = new SpotifyAuthStore(path);

        store.Save("token");
        store.Clear();
        Assert.Null(store.Load());
        Assert.False(System.IO.File.Exists(path));
    }
}
