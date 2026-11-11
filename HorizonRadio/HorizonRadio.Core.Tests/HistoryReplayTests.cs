using HorizonRadio.Core.History;
using HorizonRadio.Core.Sources.Queue;
using HorizonRadio.Core.Sources.Spotify;
using HorizonRadio.Core.Sources.YouTube;
using HorizonRadio.Sdk.Models;
using HorizonRadio.Sdk.Sources;

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

    [Fact]
    public void A_track_is_added_elsewhere_as_its_origin_named_after_the_song()
    {
        var track = new Track(" Melt ", "ryo", null, null, "youtube", "YouTube", ExternalId: "youtube:abc");

        Assert.Equal(
            new ContentRef(YouTubeSourceFactory.SourceId, "https://www.youtube.com/watch?v=abc", "Melt"),
            HistoryReplay.OriginOf(track));
    }

    [Fact]
    public void A_radio_song_has_no_origin_to_add()
    {
        var song = new Track("Melt", "ryo", null, null, "radio", "Radio", ExternalId: "radio:ryo - Melt");
        Assert.Null(HistoryReplay.OriginOf(song));
    }

    [Fact]
    public void Queue_previews_carry_what_plays_them()
    {
        var track = new Track("Song", "Artist", null, null, "local", "Local Files", ExternalId: @"local:C:\Music\Song.mp3");
        Assert.Equal(new ContentRef("local", @"C:\Music\Song.mp3", "Song"), QueuePreview.ForTrack(track).Origin);

        var entry = new ContentRef("spotify-driven", "spotify:playlist:abc", "Road Trip");
        Assert.Equal(entry, QueuePreview.ForEntry(entry).Origin);
    }
}
