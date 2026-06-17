using System.Linq;
using HorizonRadio.Core.History;
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
}
