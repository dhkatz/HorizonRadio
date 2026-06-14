using System.Text.Json;
using HorizonRadio.Core.Metadata.Apple;

namespace HorizonRadio.Core.Tests;

/// <summary>
/// The iTunes result selection: skip a fuzzy non-match, lift the matching song's fields,
/// upscale the artwork URL, and return nothing when no result actually matches.
/// </summary>
public class ItunesProviderTests
{
    private static JsonElement Json(string s) => JsonDocument.Parse(s).RootElement;

    [Fact]
    public void SelectMatch_skips_nonmatch_and_picks_the_real_song()
    {
        var json = Json("""
        {
          "resultCount": 2,
          "results": [
            { "trackName": "Totally Different", "artistName": "Someone",
              "artworkUrl100": "https://x/y/100x100bb.jpg" },
            { "trackName": "Sacred Secret", "artistName": "MuryokuP", "collectionName": "Best Of",
              "releaseDate": "2015-05-01T07:00:00Z",
              "artworkUrl100": "https://example.com/a/b/100x100bb.jpg" }
          ]
        }
        """);

        var m = ItunesProvider.SelectMatch(json, "[Megurine Luka]Sacred Secret [SEV]", "MuryokuP");

        Assert.NotNull(m);
        Assert.Equal("Sacred Secret", m!.Title);
        Assert.Equal("MuryokuP", m.Artist);
        Assert.Equal("Best Of", m.Album);
        Assert.Equal(2015, m.Year);
        Assert.Equal("https://example.com/a/b/600x600bb.jpg", m.ArtworkUrl);
    }

    [Fact]
    public void SelectMatch_returns_null_when_nothing_matches()
    {
        var json = Json("""
        {
          "resultCount": 1,
          "results": [
            { "trackName": "Wildly Unrelated", "artistName": "Nobody",
              "artworkUrl100": "https://x/y/100x100bb.jpg" }
          ]
        }
        """);

        Assert.Null(ItunesProvider.SelectMatch(json, "Sacred Secret", "MuryokuP"));
    }

    [Fact]
    public void SelectMatch_handles_empty_results()
        => Assert.Null(ItunesProvider.SelectMatch(Json("""{ "resultCount": 0, "results": [] }"""), "X", "Y"));

    [Fact]
    public void SelectMatch_prefers_the_artist_that_agrees_among_title_matches()
    {
        var json = Json("""
        {
          "results": [
            { "trackName": "Sacred Secret", "artistName": "Cover Band", "collectionName": "Covers",
              "artworkUrl100": "https://x/cover/100x100bb.jpg" },
            { "trackName": "Sacred Secret", "artistName": "MuryokuP", "collectionName": "Original",
              "artworkUrl100": "https://x/orig/100x100bb.jpg" }
          ]
        }
        """);

        var m = ItunesProvider.SelectMatch(json, "Sacred Secret", "MuryokuP");

        Assert.NotNull(m);
        Assert.Equal("MuryokuP", m!.Artist);   // artist agreement breaks the tie
        Assert.Equal("Original", m.Album);
    }

    [Fact]
    public void SelectMatch_rejects_a_same_title_by_an_unrelated_artist()
    {
        // The "Beyond the Sky" class: exact title, totally different act → no match, so we
        // fall back to the station logo rather than show the wrong cover.
        var json = Json("""
        {
          "results": [
            { "trackName": "Beyond the Sky", "artistName": "Dreams of Gray", "collectionName": "A Beginning - Single",
              "artworkUrl100": "https://x/v/100x100bb.jpg" }
          ]
        }
        """);

        Assert.Null(ItunesProvider.SelectMatch(json, "[Hatsune Miku] Beyond the Sky", "hano"));
    }
}
