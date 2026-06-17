using HorizonRadio.Core.History;
using HorizonRadio.Core.Sources.Spotify;
using HorizonRadio.Core.Sources.YouTube;

namespace HorizonRadio.Core.Tests;

/// <summary>
/// Deriving a song-level replay handle from a track's stable id: each source's id namespace
/// maps to a queueable (source, locator) pair, and the non-re-addressable cases return nothing.
/// </summary>
public class HistoryReplayTests
{
    [Fact]
    public void Spotify_uri_replays_through_the_driven_factory()
    {
        var (sourceId, locator) = HistoryReplay.DeriveOrigin("spotify:track:6ikPHWdz");
        Assert.Equal(SpotifyContentSourceFactory.SourceId, sourceId); // "spotify-driven", the queueable one
        Assert.Equal("spotify:track:6ikPHWdz", locator);
    }

    [Fact]
    public void YouTube_id_rebuilds_the_watch_url()
    {
        var (sourceId, locator) = HistoryReplay.DeriveOrigin("youtube:dQw4w9WgXcQ");
        Assert.Equal(YouTubeSourceFactory.SourceId, sourceId);
        Assert.Equal("https://www.youtube.com/watch?v=dQw4w9WgXcQ", locator);
    }

    [Fact]
    public void Local_id_yields_the_file_path()
    {
        var (sourceId, locator) = HistoryReplay.DeriveOrigin(@"local:C:\Music\song.flac");
        Assert.Equal("local", sourceId);
        Assert.Equal(@"C:\Music\song.flac", locator);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("radio:Artist - Title")] // a live stream's song isn't re-addressable
    [InlineData("youtube:")]             // malformed: no id
    [InlineData("local:")]               // malformed: no path
    [InlineData("something:else")]       // unknown namespace
    public void Non_addressable_ids_return_nothing(string? externalId)
    {
        var (sourceId, locator) = HistoryReplay.DeriveOrigin(externalId);
        Assert.Null(sourceId);
        Assert.Null(locator);
    }
}
