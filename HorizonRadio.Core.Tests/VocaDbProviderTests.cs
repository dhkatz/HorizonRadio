using System.Collections.Generic;
using System.Text.Json;
using HorizonRadio.Core.Diagnostics;
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
        Assert.Equal("https://vocadb/x/hqdefault.jpg", m.ArtUrls[0]);
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
        Assert.Equal("https://nico/t.jpg", m!.ArtUrls[0]);
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
        Assert.Equal("https://nico/t.jpg", m!.ArtUrls[0]);
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
    public void SelectMatch_returns_all_image_urls_so_a_dead_original_can_fall_through()
    {
        // urlOriginal is often a YouTube hqdefault that 404s when the source video is gone; the
        // urlThumb mirror still resolves. Both must be returned, original first, so the downloader
        // can fall through (the "ココロ、ユラユラ" art gap).
        var json = Json("""
        {
          "items": [
            { "name": "Sacred Secret", "artistString": "MuryokuP",
              "mainPicture": { "urlOriginal": "https://i1.ytimg.com/vi/x/hqdefault.jpg",
                               "urlThumb": "https://i2.hdslb.com/mirror.jpg" } }
          ]
        }
        """);

        var m = VocaDbProvider.SelectMatch(json, "Sacred Secret", "MuryokuP");
        Assert.Equal(new[] { "https://i1.ytimg.com/vi/x/hqdefault.jpg", "https://i2.hdslb.com/mirror.jpg" }, m!.ArtUrls);
    }

    [Fact]
    public void SelectMatch_surfaces_the_original_version_id_for_an_artless_remaster()
    {
        // "Re-Confliction" (a Remaster) has no art of its own but links to the original "Confliction"
        // (id 54185), which does — surface that id so the provider can borrow the original's image.
        var json = Json("""
        {
          "items": [
            { "name": "Re-Confliction", "artistString": "kouki feat. GUMI", "originalVersionId": 54185 }
          ]
        }
        """);

        var m = VocaDbProvider.SelectMatch(json, "Re-Confliction", "kouki");
        Assert.NotNull(m);
        Assert.Empty(m!.ArtUrls);                 // the remaster itself has no image
        Assert.Equal(54185, m.OriginalVersionId); // …but points at the original that does
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
        Assert.Equal("https://nico/t.jpg", m!.ArtUrls[0]);
    }

    [Fact]
    public void SelectMatch_handles_empty_items()
        => Assert.Null(VocaDbProvider.SelectMatch(Json("""{ "items": [] }"""), "X", "Y"));

    [Fact]
    public void SelectMatch_title_only_rejects_an_ambiguous_title()
    {
        // No artist + two different artists with the exact title → ambiguous, reject (千本桜 case).
        var json = Json("""
        {
          "items": [
            { "name": "千本桜", "artistString": "Cover A", "thumbUrl": "https://nico/a.jpg" },
            { "name": "千本桜", "artistString": "Cover B", "thumbUrl": "https://nico/b.jpg" }
          ]
        }
        """);

        Assert.Null(VocaDbProvider.SelectMatch(json, "千本桜", "", artistConfirmed: false));
    }

    [Fact]
    public void SelectMatch_title_only_rejects_when_matches_have_blank_artists()
    {
        // Two same-title entries with no artistString both key to "" — they must still count as
        // ambiguous/unverifiable, not collapse into "one artist owns the title".
        var json = Json("""
        {
          "items": [
            { "name": "千本桜", "thumbUrl": "https://nico/a.jpg" },
            { "name": "千本桜", "thumbUrl": "https://nico/b.jpg" }
          ]
        }
        """);

        Assert.Null(VocaDbProvider.SelectMatch(json, "千本桜", "", artistConfirmed: false));
    }

    [Fact]
    public void SelectMatch_title_only_accepts_when_one_artist_owns_the_title()
    {
        // A distinctive title owned by one artist → safe to accept on title alone.
        var json = Json("""
        { "items": [ { "name": "狂騒ノ現", "artistString": "Wonderful★opportunity!", "thumbUrl": "https://nico/t.jpg" } ] }
        """);

        var m = VocaDbProvider.SelectMatch(json, "狂騒ノ現", "", artistConfirmed: false);
        Assert.Equal("https://nico/t.jpg", m!.ArtUrls[0]);
    }

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

    [Fact]
    public void SelectMatch_prefers_an_equal_scoring_candidate_that_has_art()
    {
        // The "starry song" case: two recordings of the same APG550 song score identically. The
        // first listed (GUMI) has no image; the second (Kagamine Rin) carries a Niconico thumbnail.
        // Art presence must break the tie so the now-playing tile isn't left blank.
        var json = Json("""
        {
          "items": [
            { "name": "starry song", "artistString": "APG550 feat. GUMI" },
            { "name": "Starry song", "artistString": "APG550 feat. 鏡音リン", "thumbUrl": "https://nico/t.jpg" }
          ]
        }
        """);

        var m = VocaDbProvider.SelectMatch(json, "starry song", "APG550", artistConfirmed: true);

        Assert.NotNull(m);
        Assert.Equal("https://nico/t.jpg", m!.ArtUrls[0]);   // the art-bearing sibling won the tie
    }

    [Fact]
    public void SelectMatch_sink_captures_every_scored_candidate_including_rejected()
    {
        // The diagnostics replay data: the sink records each candidate the guard saw, with the
        // score it gave (null = rejected). Here one song matches and one same-titled track by an
        // unrelated artist is rejected — both must land in the sink so a trace line can be
        // re-scored offline to test a guard change.
        var json = Json("""
        {
          "items": [
            { "name": "Sacred Secret", "artistString": "MuryokuP feat. Megurine Luka" },
            { "name": "Sacred Secret", "artistString": "Unrelated Band" }
          ]
        }
        """);

        var sink = new List<MetadataTrace.CatalogCandidate>();
        var m = VocaDbProvider.SelectMatch(json, "Sacred Secret", "MuryokuP", artistConfirmed: false, sink: sink);

        Assert.Equal("Sacred Secret", m!.Name);
        Assert.Equal(2, sink.Count);                                   // both candidates recorded
        Assert.All(sink, c => Assert.Equal("Sacred Secret", c.Title));
        Assert.NotNull(sink[0].Score);                                 // matched the broadcast artist
        Assert.Null(sink[1].Score);                                    // rejected: unrelated artist
    }

    // -- PV links (play history replay sources) --

    [Fact]
    public void SelectPvs_keeps_the_best_pv_per_streamable_service()
    {
        var json = Json("""
        {
          "pvs": [
            { "service": "Youtube", "url": "https://youtu.be/reprint", "pvType": "Reprint", "disabled": false },
            { "service": "Youtube", "url": "https://youtu.be/original", "pvType": "Original", "disabled": false },
            { "service": "NicoNicoDouga", "url": "https://www.nicovideo.jp/watch/sm1", "pvType": "Original", "disabled": false }
          ]
        }
        """);

        var pvs = VocaDbProvider.SelectPvs(json);

        Assert.Equal(2, pvs.Count);
        Assert.Equal("YouTube", pvs[0].Service);                       // first-seen service order
        Assert.Equal("https://youtu.be/original", pvs[0].Url);          // Original beats Reprint
        Assert.Equal("Niconico", pvs[1].Service);                       // friendly label, not "NicoNicoDouga"
    }

    [Fact]
    public void SelectPvs_skips_disabled_and_non_streamable_services()
    {
        var json = Json("""
        {
          "pvs": [
            { "service": "Youtube", "url": "https://youtu.be/dead", "pvType": "Original", "disabled": true },
            { "service": "Piapro", "url": "https://piapro.jp/x", "pvType": "Original", "disabled": false },
            { "service": "SoundCloud", "url": "https://soundcloud.com/a/b", "pvType": "Original", "disabled": false }
          ]
        }
        """);

        var pvs = VocaDbProvider.SelectPvs(json);

        var only = Assert.Single(pvs);
        Assert.Equal("SoundCloud", only.Service);                       // disabled YouTube + non-streamable Piapro dropped
    }

    [Fact]
    public void SelectAlbumId_prefers_a_primary_release_over_a_compilation()
    {
        var json = Json("""
        {
          "albums": [
            { "id": 1, "discType": "Compilation", "coverPictureMime": "image/jpeg", "releaseDate": { "year": 2014, "month": 11, "day": 3, "isEmpty": false } },
            { "id": 2, "discType": "Single", "coverPictureMime": "image/jpeg", "releaseDate": { "year": 2015, "month": 1, "day": 1, "isEmpty": false } }
          ]
        }
        """);

        Assert.Equal(2, VocaDbProvider.SelectAlbumId(json)); // Single (rank) beats the earlier Compilation
    }

    [Fact]
    public void SelectAlbumId_prefers_the_earliest_among_primary_releases_and_skips_coverless()
    {
        var json = Json("""
        {
          "albums": [
            { "id": 5, "discType": "Album", "coverPictureMime": "image/jpeg", "releaseDate": { "year": 2016, "month": 5, "day": 1, "isEmpty": false } },
            { "id": 6, "discType": "Single", "coverPictureMime": "image/jpeg", "releaseDate": { "year": 2014, "month": 2, "day": 1, "isEmpty": false } },
            { "id": 7, "discType": "Single", "coverPictureMime": "", "releaseDate": { "year": 2008, "month": 1, "day": 1, "isEmpty": false } }
          ]
        }
        """);

        Assert.Equal(6, VocaDbProvider.SelectAlbumId(json)); // earliest primary WITH a cover (7 has none)
    }

    [Fact]
    public void SelectAlbumId_null_when_no_album_has_a_cover()
        => Assert.Null(VocaDbProvider.SelectAlbumId(Json("""
        { "albums": [ { "id": 9, "discType": "Album", "coverPictureMime": "" } ] }
        """)));

    [Fact]
    public void SelectMatch_prepends_the_album_cover_ahead_of_the_video_thumbnail()
    {
        var json = Json("""
        {
          "items": [
            { "name": "Sacred Secret", "artistString": "MuryokuP", "thumbUrl": "https://nico/t.jpg",
              "albums": [ { "id": 42, "discType": "Single", "coverPictureMime": "image/jpeg",
                            "releaseDate": { "year": 2011, "month": 8, "day": 1, "isEmpty": false } } ] }
          ]
        }
        """);

        var m = VocaDbProvider.SelectMatch(json, "Sacred Secret", "MuryokuP");

        Assert.Equal("https://static.vocadb.net/img/Album/mainOrig/42.jpg", m!.ArtUrls[0]); // square cover first
        Assert.Contains("https://nico/t.jpg", m.ArtUrls);                                    // thumbnail kept as fallback
        Assert.Equal(42, m.AlbumId);
    }
}
