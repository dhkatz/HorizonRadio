using System.Collections.Generic;
using System.Text.Json;
using HorizonRadio.Core.Metadata;
using HorizonRadio.Core.Metadata.Apple;
using HorizonRadio.Core.Metadata.MusicBrainz;
using HorizonRadio.Core.Metadata.VocaDb;
using HorizonRadio.Core.Models;

namespace HorizonRadio.Core.Tests;

/// <summary>
/// Stations often credit a song to the voice that sings it ("Hatsune Miku - Melt"). A voice sings
/// thousands of songs, so that credit can't confirm a match the way a producer's does: the song is
/// the voice's best-known original with exactly that name, and its artist is the producer.
/// </summary>
public class VoiceCreditTests
{
    [Theory]
    [InlineData("Hatsune Miku")]
    [InlineData("初音ミク")]
    [InlineData("Gumi English")]
    [InlineData("GUMI Whisper")]
    [InlineData("Luka x Len")]
    [InlineData("巡音ルカV4X")]
    [InlineData("v flower")]
    [InlineData("初音ミク V3 (Dark)")]
    public void A_voice_alone_is_a_voice_credit(string artist) =>
        Assert.True(Voicebanks.IsVoicebankCredit(artist));

    [Theory]
    [InlineData("ryo feat. 初音ミク")]
    [InlineData("Clean Tears")]
    [InlineData("Taishi feat. IA")]
    [InlineData("AVTechNO!")]
    [InlineData("")]
    [InlineData(null)]
    public void A_credit_naming_a_producer_is_not(string? artist) =>
        Assert.False(Voicebanks.IsVoicebankCredit(artist));

    [Fact]
    public void A_voice_credit_does_not_confirm_a_loose_title()
    {
        // "【初音ミク】Migratory" is Clean Tears' song, not "migratory birds" that Miku also sings.
        Assert.Null(SearchTerms.MatchScore("Migratory", "初音ミク", "migratory birds", "かいつぶり feat. 初音ミク"));
        Assert.NotNull(SearchTerms.MatchScore("Melt", "Hatsune Miku", "Melt (feat. Hatsune Miku)", "Hatsune Miku & supercell"));
    }

    [Fact]
    public void A_one_word_title_must_be_the_whole_name()
    {
        Assert.Null(SearchTerms.MatchScore("air", "8#Prince", "The Fresh Prince of Bel-Air", "DJ Jazzy Jeff & The Fresh Prince"));
        Assert.NotNull(SearchTerms.MatchScore("air", "8#Prince", "air", "8#Prince"));
        Assert.NotNull(SearchTerms.MatchScore("Dream", "PTaka", "Dream - BP Mix", "PTaka"));
        // A remix credit makes it more than a word, however the catalog writes it.
        Assert.NotNull(SearchTerms.MatchScore("Leia (Hachioji P Remix)", "8#Prince", "Leia -八王子P Remix-",
            "八王子P feat. 巡音ルカ", artistConfirmed: true));
    }

    [Fact]
    public void A_lone_latin_word_with_no_artist_is_anyones_song()
    {
        Assert.Null(SearchTerms.MatchScore("ARiA", null, "~ARiA~", "RuiKagenePlum feat. 歌手音ピコ"));
        Assert.NotNull(SearchTerms.MatchScore("千本桜", null, "千本桜", "黒うさP feat. 初音ミク"));
    }

    [Fact]
    public void The_original_release_outranks_a_live_or_remixed_one()
    {
        var original = SearchTerms.MatchScore("Lost One's Weeping", "Neru", "Lost One's Weeping", "Neru");
        var rerecorded = SearchTerms.MatchScore("Lost One's Weeping", "Neru", "Lost One's Weeping (2025 New Recording)", "Neru");
        var remix = SearchTerms.MatchScore("Melt", "supercell", "Melt - CPK! Remix", "ryo (supercell)");
        var melt = SearchTerms.MatchScore("Melt", "supercell", "Melt", "supercell");

        Assert.True(original > rerecorded);
        Assert.True(melt > remix);
        // Asked for by name, the remix isn't marked down.
        Assert.True(SearchTerms.MatchScore("Melt CPK! Remix", "supercell", "Melt - CPK! Remix", "ryo (supercell)") >
                    SearchTerms.MatchScore("Melt", "supercell", "Melt - CPK! Remix", "ryo (supercell)"));
    }

    [Fact]
    public void A_title_only_search_counts_versions_credited_in_brackets()
    {
        // iTunes had one "千本桜" by an artist named after a lyric, and many "千本桜 (feat. 初音ミク)":
        // that's a widely covered song, not one artist's.
        var json = JsonDocument.Parse("""
            { "results": [
              { "trackName": "千本桜", "artistName": "まだ見たことのないセカイ" },
              { "trackName": "千本桜 (feat. 初音ミク)", "artistName": "黒うさP" },
              { "trackName": "千本桜 (feat. 初音ミク)", "artistName": "WhiteFlame" } ] }
            """).RootElement;

        Assert.Null(ItunesProvider.SelectMatch(json, "千本桜", null));
    }

    [Fact]
    public void VocaDB_takes_the_most_popular_original_named_exactly_so()
    {
        // FindByVoiceAsync's search: the voice's originals, most popular first.
        var json = JsonDocument.Parse("""
            { "items": [
              { "id": 1322, "name": "メルト", "artistString": "ryo feat. 初音ミク", "names": [ { "value": "メルト" }, { "value": "Melt" } ] },
              { "id": 884765, "name": "Melt ice cream", "artistString": "트랩칙 feat. 初音ミク" },
              { "id": 9, "name": "Melt", "artistString": "Someone feat. 初音ミク" } ] }
            """).RootElement;

        var m = VocaDbProvider.SelectMatch(json, "Melt", "Hatsune Miku", artistConfirmed: false, sink: null, out _, byRating: true);

        Assert.Equal(1322, m?.Id);
        Assert.Equal("ryo feat. 初音ミク", m?.Artist);
    }

    [Fact]
    public void MusicBrainz_takes_art_from_the_songs_own_release_before_a_live_or_compilation_one()
    {
        var releases = JsonDocument.Parse("""
            [ { "id": "live", "release-group": { "primary-type": "Album", "secondary-types": ["Live"] } },
              { "id": "best", "release-group": { "primary-type": "Album", "secondary-types": ["Compilation"] } },
              { "id": "single", "release-group": { "primary-type": "Single" } } ]
            """).RootElement;

        Assert.Equal(["single", "best", "live"],
            MusicBrainzProvider.OrderReleases(releases).Select(r => r.GetProperty("id").GetString()));
    }

    // A catalog that answers one (artist, title) and records what it was asked.
    private sealed class Catalog(string id, bool identifies, string artist, string title, MetadataContribution answer) : IMetadataProvider
    {
        public readonly List<string> Asked = [];
        public string Id => id;
        public bool IdentifiesOriginals => identifies;

        public Task<MetadataContribution?> ContributeAsync(MetadataQuery q, CancellationToken ct)
        {
            lock (Asked) Asked.Add($"{q.Artist}|{q.Title}");
            return Task.FromResult(string.Equals(q.Artist, artist, StringComparison.Ordinal) && q.Title == title ? answer : null);
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private static MetadataPolicy Policy(params string[] order) => new([MetadataPolicy.SourceId, .. order], new Dictionary<MetadataField, string>());

    [Fact]
    public async Task A_voice_credit_asks_the_catalog_that_knows_originals_first_and_shows_its_artist()
    {
        // The store comes first in the user's order, but it can only confirm the producer it's told.
        var store = new Catalog("itunes", identifies: false, "ryo feat. 初音ミク", "Melt",
            new MetadataContribution(Artist: "supercell", Album: "supercell"));
        var vocadb = new Catalog("vocadb", identifies: true, "Hatsune Miku", "Melt",
            new MetadataContribution(Title: "メルト", Artist: "ryo feat. 初音ミク"));
        var resolver = new MetadataResolver();
        resolver.Configure([store, vocadb], Policy("itunes", "vocadb"));

        var r = await resolver.ResolveDetailedAsync(new Track("Melt", "Hatsune Miku", null, null, "radio", "Internet Radio"), CancellationToken.None);

        Assert.True(r.Matched);
        Assert.Equal("supercell", r.Track.Artist);
        Assert.Equal("supercell", r.Track.Album);
        Assert.Equal(["ryo feat. 初音ミク|Melt"], store.Asked);
    }

    [Fact]
    public async Task A_producer_reading_is_tried_before_a_voice_reading()
    {
        // "keerosah - [初音ミク] Migratory [Clean Tears]": the vocalist tag is the weaker reading.
        var catalog = new Catalog("itunes", identifies: false, "Clean Tears", "Migratory",
            new MetadataContribution(Artist: "Clean Tears", Album: "Heat Haze"));
        var resolver = new MetadataResolver();
        resolver.Configure([catalog], Policy("itunes"));
        var seed = new Track("Migratory", "keerosah", null, null, "radio", "Internet Radio",
            Candidates: [new TitleCandidate("初音ミク", "Migratory"), new TitleCandidate("Clean Tears", "Migratory")]);

        var r = await resolver.ResolveDetailedAsync(seed, CancellationToken.None);

        Assert.Equal("Clean Tears", r.Track.Artist);
        Assert.DoesNotContain("初音ミク|Migratory", catalog.Asked);
    }
}
