using System.Text.Json;
using HorizonRadio.Core.Metadata.VocaDb;

namespace HorizonRadio.Core.Tests;

/// <summary>
/// VocaDB result selection: match a producer track by its title against the combined
/// producer+vocalist artist string, lift its image + year, and reject a fuzzy
/// name-substring hit by an unrelated artist.
/// </summary>
public class VocaDbProviderTests
{
    private static JsonElement Json(string s) => JsonDocument.Parse(s).RootElement;

    [Fact]
    public void SelectMatch_matches_producer_track_via_artist_string()
    {
        var json = Json("""
        {
          "items": [
            { "name": "Sacred Secret", "artistString": "MuryokuP feat. Megurine Luka",
              "publishDate": "2011-08-01T00:00:00Z",
              "mainPicture": { "urlOriginal": "https://vocadb/x/hqdefault.jpg" } }
          ]
        }
        """);

        // Broadcast artist "MuryokuP" overlaps the "MuryokuP feat. Megurine Luka" credit.
        var m = VocaDbProvider.SelectMatch(json, "[Megurine Luka]Sacred Secret [SEV]", "MuryokuP");

        Assert.NotNull(m);
        Assert.Equal("Sacred Secret", m!.Name);
        Assert.Equal(2011, m.Year);
        Assert.Equal("https://vocadb/x/hqdefault.jpg", m.ArtUrl);
    }

    [Fact]
    public void SelectMatch_matches_a_katakana_title_against_an_english_name_variant()
    {
        // VocaDB returns the primary name English ("Sacrifice") under lang=English, but the
        // broadcast title is katakana ("サクリファイス"). The Names field carries the Japanese
        // variant; artist already confirmed via artistId scoping, so the title alone qualifies.
        var json = Json("""
        {
          "items": [
            { "name": "Sacrifice", "artistString": "ItachimaP feat. Megurine Luka, Hatsune Miku",
              "thumbUrl": "https://nico/t.jpg",
              "names": [ { "language": "Japanese", "value": "サクリファイス" },
                         { "language": "English", "value": "Sacrifice" } ] }
          ]
        }
        """);

        var m = VocaDbProvider.SelectMatch(json, "サクリファイス", "Itachima-p", artistConfirmed: true);

        Assert.NotNull(m);
        Assert.Equal("https://nico/t.jpg", m!.ArtUrl);
    }

    [Fact]
    public void SelectMatch_matches_a_romaji_query_against_a_kanji_artist_via_cross_script()
    {
        // The "Innocent Favor / Bunmyaku" case: under lang=Default VocaDB returns the artist
        // natively ("文脈 feat. GUMI"). The romaji broadcast name "Bunmyaku" can't token-match the
        // kanji — but that's a script difference, not a contradiction, so an exact multi-word title
        // carries it (plain path, artist NOT pre-confirmed).
        var json = Json("""
        {
          "items": [
            { "name": "Innocent Favor", "artistString": "文脈 feat. GUMI", "thumbUrl": "https://nico/t.jpg",
              "names": [ { "language": "English", "value": "Innocent Favor" } ] }
          ]
        }
        """);

        var m = VocaDbProvider.SelectMatch(json, "Innocent Favor", "Bunmyaku", artistConfirmed: false);

        Assert.NotNull(m);
        Assert.Equal("https://nico/t.jpg", m!.ArtUrl);
    }

    [Fact]
    public void SelectMatch_rejects_name_substring_by_unrelated_artist()
    {
        // VocaDB's Auto match returns "Beyond the Sky" ⊂ "A Place beyond the starry sky"
        // (title overlap is high), but the artist is a different act → reject.
        var json = Json("""
        {
          "items": [
            { "name": "A Place beyond the starry sky", "artistString": "MIJIPIN feat. MEIKO",
              "mainPicture": { "urlOriginal": "https://vocadb/y/thumb.jpg" } }
          ]
        }
        """);

        Assert.Null(VocaDbProvider.SelectMatch(json, "Beyond the Sky", "hano"));
    }

    [Fact]
    public void SelectMatch_falls_back_to_thumbUrl_when_no_main_picture()
    {
        var json = Json("""
        {
          "items": [
            { "name": "Sacred Secret", "artistString": "MuryokuP", "thumbUrl": "https://nico/t.jpg" }
          ]
        }
        """);

        var m = VocaDbProvider.SelectMatch(json, "Sacred Secret", "MuryokuP");
        Assert.Equal("https://nico/t.jpg", m!.ArtUrl);
    }

    [Fact]
    public void SelectMatch_handles_empty_items()
        => Assert.Null(VocaDbProvider.SelectMatch(Json("""{ "items": [] }"""), "X", "Y"));

    [Fact]
    public void SelectArtistIds_prefers_the_producer_over_same_named_illustrators()
    {
        // Exact "hano" returns several entries; the Producer is the song's likely artist.
        var json = Json("""
        {
          "items": [
            { "id": 75441, "name": "HANO", "artistType": "OtherGroup" },
            { "id": 49577, "name": "Hano", "artistType": "Illustrator" },
            { "id": 26748, "name": "hano", "artistType": "Producer" },
            { "id": 104039, "name": "HANO", "artistType": "Animator" }
          ]
        }
        """);

        var ids = VocaDbProvider.SelectArtistIds(json, max: 2);

        Assert.Equal(26748, ids[0]);   // Producer first
        Assert.Equal(75441, ids[1]);   // then the group, ahead of illustrator/animator
    }

    [Fact]
    public void SelectArtistIds_empty_when_no_items()
        => Assert.Empty(VocaDbProvider.SelectArtistIds(Json("""{ "items": [] }"""), 2));
}
