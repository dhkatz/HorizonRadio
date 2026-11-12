using System.Text.Json;
using HorizonRadio.Core.Lyrics;
using HorizonRadio.Core.Settings;
using HorizonRadio.Metadata.VocaDb;
using HorizonRadio.Sdk.Lyrics;

namespace HorizonRadio.Core.Tests;

public class KaraokeTests
{
    private static TimeSpan S(double seconds) => TimeSpan.FromSeconds(seconds);

    [Fact]
    public void Syllables_of_a_word_fill_as_one_word()
    {
        var line = new LyricLine(S(1), "Hello world", Words:
        [
            new LyricWord(S(1), S(1.5), "Hel"),
            new LyricWord(S(1.5), S(2), "lo "),
            new LyricWord(S(2), S(3), "world"),
        ]);

        var words = Karaoke.Words(line, S(10));

        Assert.Equal(["Hello", "world"], words.Select(w => w.Text));
        Assert.True(words[0].SpaceAfter);
        Assert.Equal(0, words[0].FillAt(S(1)));
        Assert.Equal(0.6, words[0].FillAt(S(1.5)), 3); // "Hel" sung: 3 of 5 characters
        Assert.Equal(1, words[0].FillAt(S(2)));
        Assert.Equal(0.5, words[1].FillAt(S(2.5)), 3);
    }

    [Fact]
    public void A_word_without_an_end_runs_to_the_next_word()
    {
        var line = new LyricLine(S(0), "a b", Words: [new LyricWord(S(0), null, "a "), new LyricWord(S(2), null, "b")]);

        var words = Karaoke.Words(line, S(4));

        Assert.Equal(0.5, words[0].FillAt(S(1)), 3);
        Assert.Equal(0.5, words[1].FillAt(S(3)), 3); // the last runs to the line's end
    }

    [Fact]
    public void Japanese_syllables_stay_separate_so_the_line_can_wrap()
    {
        var line = new LyricLine(S(0), "無敵の笑顔", Words:
            [new LyricWord(S(0), S(1), "無敵"), new LyricWord(S(1), S(2), "の"), new LyricWord(S(2), S(3), "笑顔")]);

        Assert.Equal(["無敵", "の", "笑顔"], Karaoke.Words(line, S(5)).Select(w => w.Text));
    }

    [Fact]
    public void A_line_timed_only_by_its_start_fills_over_most_of_the_gap_to_the_next()
    {
        // 10 s to the next line: filled by 9 s, spread by characters ("aaaa" then "bbbb").
        var words = Karaoke.Words(new LyricLine(S(0), "aaaa bbbb"), S(10));

        Assert.Equal(["aaaa", "bbbb"], words.Select(w => w.Text));
        Assert.Equal(0.5, words[0].FillAt(S(2.25)), 3);
        Assert.Equal(0, words[1].FillAt(S(4.5)), 3);
        Assert.Equal(1, words[1].FillAt(S(9)), 3);
    }

    [Fact]
    public void A_line_timed_only_by_its_start_splits_japanese_into_characters()
    {
        var words = Karaoke.Words(new LyricLine(S(0), "正しさとは 愚かさとは"), S(10));

        Assert.Equal(10, words.Count);
        Assert.True(words[4].SpaceAfter);  // "は" ends the first phrase
        Assert.False(words[0].SpaceAfter);
        Assert.False(words[^1].SpaceAfter);
    }

    [Fact]
    public void A_short_line_still_takes_half_a_second()
    {
        var word = Assert.Single(Karaoke.Words(new LyricLine(S(5), "hey"), S(5.1)));

        Assert.Equal(0.5, word.FillAt(S(5.25)), 3);
    }
}

public class VocaDbLyricsTests
{
    private static JsonElement Song(string lyrics) => JsonDocument.Parse($$"""{"id":1,"lyrics":{{lyrics}}}""").RootElement;

    [Fact]
    public void Original_with_romaji_and_translation_as_versions()
    {
        var lyrics = VocaDbLyricsProvider.SelectLyrics(Song("""
            [
              {"cultureCodes":["en"],"translationType":"Translation","value":"I can't keep up\nwith you"},
              {"cultureCodes":["ja"],"translationType":"Original","value":"くだらない戯言\n\n\nあたしが言いたいこと \n\n"},
              {"cultureCodes":["und"],"translationType":"Romanized","value":"kudaranai tawagoto"},
              {"cultureCodes":["en"],"translationType":"Translation","value":"another English one"}
            ]
            """))!;

        Assert.False(lyrics.Synced);
        Assert.Equal(["くだらない戯言", "", "あたしが言いたいこと"], lyrics.Lines.Select(l => l.Text)); // one blank between verses
        Assert.Equal(["Romaji", "English"], lyrics.Variants!.Select(v => v.Label)); // one per language
        Assert.Equal("Lyrics from VocaDB", lyrics.Credit);
    }

    [Fact]
    public void Only_a_translation_is_still_lyrics()
    {
        var lyrics = VocaDbLyricsProvider.SelectLyrics(Song("""[{"cultureCode":"en","translationType":"Translation","value":"hello"}]"""));

        Assert.Equal("hello", lyrics!.Lines[0].Text);
        Assert.Null(lyrics.Variants);
    }

    [Fact]
    public void No_lyrics_is_null() => Assert.Null(VocaDbLyricsProvider.SelectLyrics(Song("[]")));
}

public class LyricsVersionPreferenceTests
{
    private sealed class Stub(SongLyrics? answer) : ILyricsProvider
    {
        public string Id => answer?.ProviderId ?? "none";
        public Task<SongLyrics?> FindAsync(LyricsQuery query, CancellationToken ct) => Task.FromResult(answer);
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    [Fact]
    public async Task Unsynced_lyrics_with_translations_beat_bare_text()
    {
        var bare = new SongLyrics("lrclib", false, [new LyricLine(TimeSpan.Zero, "a")]);
        var rich = new SongLyrics("vocadb", false, [new LyricLine(TimeSpan.Zero, "a")],
            Variants: [new LyricsVariant("English", [new LyricLine(TimeSpan.Zero, "b")])]);
        var service = new LyricsService([new Stub(bare), new Stub(rich)]);

        Assert.Equal("vocadb", (await service.FindAsync(new("Song", "Band"), CancellationToken.None))!.ProviderId);
    }

    [Fact]
    public void Karaoke_and_version_choices_survive_a_restart()
    {
        using var dir = TempDir.Create();
        var path = Path.Combine(dir.Path, SettingsStore.FileName);
        using (var store = SettingsStore.Open(path))
        {
            store.Set(LyricsSettings.Karaoke, false);
            store.Set(LyricsSettings.Version, "Romaji");
        }

        using var loaded = SettingsStore.Open(path);
        Assert.False(loaded.Get(LyricsSettings.Karaoke));
        Assert.Equal("Romaji", loaded.Get(LyricsSettings.Version));
        Assert.True(SettingsStore.CreateInMemory().Get(LyricsSettings.Karaoke)); // on by default
    }
}
