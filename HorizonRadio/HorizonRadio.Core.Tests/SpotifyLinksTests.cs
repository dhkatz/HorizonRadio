using HorizonRadio.Core.Sources.Spotify;

namespace HorizonRadio.Core.Tests;

/// <summary>
/// The Spotify locator parser accepts the URI and web-link forms a user can paste
/// (track/playlist/album), strips share queries and localized paths, and rejects
/// everything else.
/// </summary>
public class SpotifyLinksTests
{
    [Theory]
    [InlineData("spotify:track:4cOdK2wGLETKBW3PvgPWqT", SpotifyLinkKind.Track, "4cOdK2wGLETKBW3PvgPWqT")]
    [InlineData("spotify:playlist:37i9dQZF1DXcBWIGoYBM5M", SpotifyLinkKind.Playlist, "37i9dQZF1DXcBWIGoYBM5M")]
    [InlineData("spotify:album:1DFixLWuPkv3KT3TnV35m3", SpotifyLinkKind.Album, "1DFixLWuPkv3KT3TnV35m3")]
    public void Parses_uri_form(string input, SpotifyLinkKind kind, string id)
    {
        Assert.True(SpotifyLinks.TryParse(input, out var link));
        Assert.Equal(kind, link.Kind);
        Assert.Equal(id, link.Id);
    }

    [Theory]
    [InlineData("https://open.spotify.com/track/4cOdK2wGLETKBW3PvgPWqT", SpotifyLinkKind.Track, "4cOdK2wGLETKBW3PvgPWqT")]
    [InlineData("https://open.spotify.com/track/4cOdK2wGLETKBW3PvgPWqT?si=abc123", SpotifyLinkKind.Track, "4cOdK2wGLETKBW3PvgPWqT")]
    [InlineData("https://open.spotify.com/intl-de/album/1DFixLWuPkv3KT3TnV35m3", SpotifyLinkKind.Album, "1DFixLWuPkv3KT3TnV35m3")]
    [InlineData("https://open.spotify.com/playlist/37i9dQZF1DXcBWIGoYBM5M?si=x", SpotifyLinkKind.Playlist, "37i9dQZF1DXcBWIGoYBM5M")]
    public void Parses_web_form(string input, SpotifyLinkKind kind, string id)
    {
        Assert.True(SpotifyLinks.TryParse(input, out var link));
        Assert.Equal(kind, link.Kind);
        Assert.Equal(id, link.Id);
    }

    [Fact]
    public void Round_trips_to_canonical_uri()
    {
        Assert.True(SpotifyLinks.TryParse("https://open.spotify.com/track/abc?si=1", out var link));
        Assert.Equal("spotify:track:abc", link.Uri);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(null)]
    [InlineData("not a link")]
    [InlineData("https://example.com/track/abc")]                 // wrong host
    [InlineData("spotify:artist:0OdUWJ0sBjDrqHygGUXeCF")]          // unsupported kind
    [InlineData("https://open.spotify.com/artist/0OdUWJ0sBjDrq")] // unsupported kind
    [InlineData("https://open.spotify.com/track/")]               // missing id
    public void Rejects_unsupported(string? input)
    {
        Assert.False(SpotifyLinks.TryParse(input, out _));
    }
}
