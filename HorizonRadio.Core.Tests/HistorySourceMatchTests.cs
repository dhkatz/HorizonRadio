using System.Linq;
using HorizonRadio.Core.History;
using HorizonRadio.Core.Metadata;
using HorizonRadio.Core.Sources;

namespace HorizonRadio.Core.Tests;

/// <summary>
/// Picking which search hits are really the song: conservative token-subset matching, one kept
/// per service (best-ranked first), so play history stores a real playable URL per service.
/// </summary>
public class HistorySourceMatchTests
{
    private static SearchResult Track(string sourceId, string title, string subtitle, string locator) =>
        new(sourceId, SearchResultKind.Track, title, subtitle, ArtUrl: null, Locator: locator);

    [Fact]
    public void Keeps_one_matching_hit_per_service_in_order()
    {
        var results = new[]
        {
            Track("youtube", "MuryokuP - Sacred Secret (Official Video)", "MuryokuP", "yt1"),
            Track("spotify-driven", "Sacred Secret", "MuryokuP", "sp1"),
        };

        var chosen = HistorySourceMatch.Select("MuryokuP", "Sacred Secret", results);

        Assert.Equal(new[] { "youtube", "spotify-driven" }, chosen.Select(r => r.SourceId));
        Assert.Equal("yt1", chosen[0].Locator);
    }

    [Fact]
    public void Excludes_hits_that_dont_cover_the_query()
    {
        var results = new[]
        {
            Track("youtube", "Some Other Song", "Another Artist", "yt-wrong"),
            Track("spotify-driven", "Sacred Secret", "MuryokuP", "sp1"),
        };

        var chosen = HistorySourceMatch.Select("MuryokuP", "Sacred Secret", results);

        Assert.Equal(new[] { "spotify-driven" }, chosen.Select(r => r.SourceId));
    }

    [Fact]
    public void Takes_the_first_matching_hit_per_service()
    {
        var results = new[]
        {
            Track("youtube", "MuryokuP - Sacred Secret", "MuryokuP", "yt-best"),
            Track("youtube", "Sacred Secret (Sacred Secret cover)", "MuryokuP", "yt-cover"),
        };

        var chosen = HistorySourceMatch.Select("MuryokuP", "Sacred Secret", results);

        var only = Assert.Single(chosen);
        Assert.Equal("yt-best", only.Locator);
    }

    [Fact]
    public void A_one_word_query_is_too_weak_to_match()
    {
        var results = new[] { Track("youtube", "Closer (anything)", "Whoever", "yt") };
        Assert.Empty(HistorySourceMatch.Select("", "Closer", results));
    }

    [Fact]
    public void Ignores_album_and_playlist_hits()
    {
        var results = new[]
        {
            new SearchResult("spotify-driven", SearchResultKind.Album, "Sacred Secret", "MuryokuP", null, "album1"),
        };
        Assert.Empty(HistorySourceMatch.Select("MuryokuP", "Sacred Secret", results));
    }

    // -- PV links → replay sources --

    [Fact]
    public void FromPvs_routes_every_pv_through_the_ytdlp_engine_keeping_its_service_label()
    {
        var pvs = new[]
        {
            new PlayableRef("YouTube", "https://youtu.be/a"),
            new PlayableRef("Niconico", "https://www.nicovideo.jp/watch/sm1"),
        };

        var sources = HistorySourceMatch.FromPvs(pvs);

        Assert.All(sources, s => Assert.Equal("youtube", s.SourceId)); // all ride the yt-dlp content factory
        Assert.Equal(new[] { "YouTube", "Niconico" }, sources.Select(s => s.SourceDisplay));
        Assert.Equal("https://www.nicovideo.jp/watch/sm1", sources[1].Locator);
    }

    [Fact]
    public void Combine_prefers_pvs_and_adds_only_services_they_dont_cover()
    {
        var pvs = HistorySourceMatch.FromPvs([new PlayableRef("YouTube", "https://youtu.be/pv")]);
        var searchHits = new[]
        {
            new ReplaySource("youtube", "YouTube", "https://www.youtube.com/watch?v=fuzzy"), // same service → dropped
            new ReplaySource("spotify-driven", "Spotify", "spotify:track:x"),                // new service → kept
        };

        var combined = HistorySourceMatch.Combine(pvs, searchHits);

        Assert.Equal(2, combined.Count);
        Assert.Equal("https://youtu.be/pv", combined[0].Locator); // exact PV preferred over the fuzzy YouTube hit
        Assert.Equal("Spotify", combined[1].SourceDisplay);
    }
}
