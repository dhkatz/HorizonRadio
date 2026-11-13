using HorizonRadio.Core.Lyrics;
using HorizonRadio.Lyrics.Lrclib;
using HorizonRadio.Lyrics.Unison;
using HorizonRadio.Sdk.Lyrics;

namespace HorizonRadio.Core.Tests;

public class LrcParserTests
{
    [Fact]
    public void Parses_timed_lines_in_order()
    {
        var (synced, lines) = LrcParser.Parse("[ti:Song]\n[00:12.50]second\n[00:01.00]first\n\n[01:02.3]third");

        Assert.True(synced);
        Assert.Equal(["first", "second", "third"], lines.Select(l => l.Text));
        Assert.Equal(TimeSpan.FromMilliseconds(12_500), lines[1].Time);
        Assert.Equal(TimeSpan.FromMilliseconds(62_300), lines[2].Time);
    }

    [Fact]
    public void A_line_with_several_stamps_repeats()
    {
        var (_, lines) = LrcParser.Parse("[00:10.00][00:40.00]chorus\n[00:20.00]verse");

        Assert.Equal(["chorus", "verse", "chorus"], lines.Select(l => l.Text));
    }

    [Fact]
    public void A_positive_offset_moves_lines_earlier()
    {
        var (_, lines) = LrcParser.Parse("[offset:+500]\n[00:10.00]a\n[00:00.20]b");

        Assert.Equal(TimeSpan.Zero, lines[0].Time); // clamped, not negative
        Assert.Equal(TimeSpan.FromMilliseconds(9_500), lines[1].Time);
    }

    [Fact]
    public void Inline_stamps_become_words()
    {
        var (_, lines) = LrcParser.Parse("[00:01.00]<00:01.00>Hello <00:01.50>there<00:02.00>");

        var line = Assert.Single(lines);
        Assert.Equal("Hello there", line.Text);
        Assert.Equal(["Hello ", "there"], line.Words!.Select(w => w.Text));
        Assert.Equal(TimeSpan.FromMilliseconds(1_500), line.Words![1].Time);
    }

    [Fact]
    public void Text_without_stamps_is_unsynced()
    {
        var (synced, lines) = LrcParser.Parse("first line\r\nsecond line\r\n");

        Assert.False(synced);
        Assert.Equal(["first line", "second line"], lines.Select(l => l.Text));
        Assert.All(lines, l => Assert.Equal(TimeSpan.Zero, l.Time));
    }
}

public class TtmlParserTests
{
    private const string Ttml = """
        <tt xmlns="http://www.w3.org/ns/ttml" xmlns:ttm="http://www.w3.org/ns/ttml#metadata"><body><div>
        <p begin="1.0" end="3.0" ttm:agent="v1"><span begin="1.0" end="1.5">Hel</span><span begin="1.5" end="2.0">lo</span> <span begin="2.0" end="3.0">world</span><span ttm:role="x-bg"><span begin="2.5" end="3.0">(ooh)</span></span></p>
        <p begin="00:04.250" end="00:05.000">plain &amp; timed</p>
        </div></body></tt>
        """;

    [Fact]
    public void Spans_are_words_and_syllables()
    {
        var (synced, lines) = TtmlParser.Parse(Ttml)!.Value;

        Assert.True(synced);
        var main = lines[0];
        Assert.Equal("Hello world", main.Text); // no space between syllables
        Assert.Equal("v1", main.Voice);
        Assert.Equal(TimeSpan.FromSeconds(1), main.Time);
        Assert.Equal(TimeSpan.FromSeconds(3), main.End);
        Assert.Equal(["Hel", "lo ", "world"], main.Words!.Select(w => w.Text));
    }

    [Fact]
    public void Backing_vocals_are_their_own_line()
    {
        var (_, lines) = TtmlParser.Parse(Ttml)!.Value;

        var bg = Assert.Single(lines, l => l.Background);
        Assert.Equal("(ooh)", bg.Text);
        Assert.Equal(TimeSpan.FromMilliseconds(2_500), bg.Time);
        Assert.DoesNotContain("ooh", lines[0].Text, StringComparison.Ordinal);
    }

    [Fact]
    public void A_line_without_spans_keeps_its_own_timing()
    {
        var (_, lines) = TtmlParser.Parse(Ttml)!.Value;

        var line = lines[^1];
        Assert.Equal("plain & timed", line.Text);
        Assert.Equal(TimeSpan.FromMilliseconds(4_250), line.Time);
    }

    [Fact]
    public void Malformed_xml_is_null() => Assert.Null(TtmlParser.Parse("<tt><body><p>oops</body>"));

    [Theory]
    [InlineData("3.5", 3_500)]
    [InlineData("2:03.5", 123_500)]
    [InlineData("1:02:03", 3_723_000)]
    [InlineData("350ms", 350)]
    [InlineData("1.5s", 1_500)]
    [InlineData("2m", 120_000)]
    public void Parses_times(string value, int ms) =>
        Assert.Equal(TimeSpan.FromMilliseconds(ms), TtmlParser.ParseTime(value));

    [Theory]
    [InlineData("")]
    [InlineData("00:00:03:12")]
    [InlineData("abc")]
    public void Rejects_unsupported_times(string value) => Assert.Null(TtmlParser.ParseTime(value));
}

public class LyricsSearchTests
{
    [Theory]
    [InlineData("Get Lucky (feat. Pharrell Williams) - Radio Edit", "Get Lucky")]
    [InlineData("Song feat. Someone", "Song")]
    [InlineData("Track - 2011 Remaster", "Track")]
    [InlineData("ヒバナ【初音ミク】", "ヒバナ")]
    [InlineData("(Intro)", "(Intro)")]
    public void Title_drops_credits_and_versions(string title, string expected) =>
        Assert.Equal(expected, LyricsSearch.Title(title));

    [Theory]
    [InlineData("Daft Punk, Pharrell Williams", "Daft Punk")]
    [InlineData("DECO*27 feat. 初音ミク", "DECO*27")]
    [InlineData("Simon & Garfunkel", "Simon & Garfunkel")]
    public void Artist_is_the_first_credit(string artist, string expected) =>
        Assert.Equal(expected, LyricsSearch.Artist(artist));
}

public class LrclibPickTests
{
    private static LrclibProvider.Record Rec(double seconds, string? synced = null, string? plain = null,
        string title = "Song", string artist = "Band") =>
        new(title, artist, seconds, Instrumental: false, plain, synced);

    // Lines every 10 s up to (and including) the given second.
    private static string Lrc(int lastSecond, string word = "line") =>
        string.Join('\n', Enumerable.Range(0, lastSecond / 10 + 1).Select(i => $"[{i * 10 / 60:00}:{i * 10 % 60:00}.00]{word} {i}"));

    private static LyricsQuery Query(double? seconds) =>
        new("Song", "Band", Duration: seconds is { } s ? TimeSpan.FromSeconds(s) : null);

    [Fact]
    public void Picks_synced_lyrics_timed_to_this_length()
    {
        var picked = LrclibProvider.Pick([Rec(300, Lrc(250, "wrong")), Rec(200, Lrc(180))], Query(201));

        Assert.True(picked!.Synced);
        Assert.Equal("line 0", picked.Lines[0].Text);
    }

    [Fact]
    public void Lyrics_that_end_far_too_early_are_not_trusted()
    {
        // Timed lines stop at 60 s of a 200 s song: they'd race ahead of the singer.
        var picked = LrclibProvider.Pick([Rec(200, Lrc(60), plain: "a\nb")], Query(200));

        Assert.False(picked!.Synced);
        Assert.Equal(["a", "b"], picked.Lines.Select(l => l.Text));
    }

    [Fact]
    public void A_file_pasted_across_cuts_loses_to_one_timed_on_its_own()
    {
        var pasted = Lrc(180, "pasted");
        var picked = LrclibProvider.Pick([Rec(200, pasted), Rec(240, pasted), Rec(201, Lrc(180, "own"))], Query(200));

        Assert.StartsWith("own", picked!.Lines[0].Text, StringComparison.Ordinal);
    }

    [Fact]
    public void Nothing_within_two_seconds_is_no_lyrics()
    {
        Assert.Null(LrclibProvider.Pick([Rec(210, Lrc(190), plain: "x")], Query(200)));
    }

    [Fact]
    public void Without_a_length_the_song_is_matched_by_name()
    {
        var picked = LrclibProvider.Pick(
            [Rec(200, Lrc(180, "other"), title: "Different Song", artist: "Someone Else"), Rec(180, Lrc(170))],
            Query(null));

        Assert.Equal("line 0", picked!.Lines[0].Text);
    }
}

public class UnisonToLyricsTests
{
    private const string Lrc = "[00:01.00]one\n[00:02.00]two";

    private static UnisonProvider.Entry Entry(string song = "Espresso", string artist = "Sabrina Carpenter",
        string? video = "abc", double score = 1, bool hidden = false, string format = "lrc") =>
        new(song, artist, video, Lrc, format, score, hidden, new UnisonProvider.Submitter("kustine"));

    private static LyricsQuery Query(string title = "Espresso", string? video = null, double seconds = 175) =>
        new(title, "Sabrina Carpenter", Duration: TimeSpan.FromSeconds(seconds), YouTubeVideoId: video);

    [Fact]
    public void Credits_the_submitter()
    {
        var lyrics = UnisonProvider.ToLyrics(Entry(), Query());

        Assert.True(lyrics!.Synced);
        Assert.Equal("Lyrics from Unison · synced by kustine", lyrics.Credit);
    }

    [Fact]
    public void Hidden_or_downvoted_lyrics_are_skipped()
    {
        Assert.Null(UnisonProvider.ToLyrics(Entry(hidden: true), Query()));
        Assert.Null(UnisonProvider.ToLyrics(Entry(score: -1), Query()));
    }

    [Fact]
    public void A_different_song_is_skipped_unless_it_is_the_same_video()
    {
        Assert.Null(UnisonProvider.ToLyrics(Entry(song: "Please Please Please"), Query()));
        Assert.NotNull(UnisonProvider.ToLyrics(Entry(song: "Please Please Please", video: "abc"), Query(video: "abc")));
    }

    [Fact]
    public void Lyrics_timed_past_the_end_are_for_another_cut()
    {
        Assert.Null(UnisonProvider.ToLyrics(Entry(), Query(seconds: 1.5)));
    }

    [Fact]
    public void Plain_format_is_unsynced()
    {
        var lyrics = UnisonProvider.ToLyrics(Entry(format: "plain"), Query());

        Assert.False(lyrics!.Synced);
        Assert.All(lyrics.Lines, l => Assert.Equal(TimeSpan.Zero, l.Time));
    }
}

public class LyricsServiceTests
{
    private sealed class Stub(string id, Func<SongLyrics?> answer) : ILyricsProvider
    {
        public string Id => id;
        public int Calls;

        public Task<SongLyrics?> FindAsync(LyricsQuery query, CancellationToken ct)
        {
            Interlocked.Increment(ref Calls);
            return Task.FromResult(answer());
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private static SongLyrics Lyrics(string provider, bool synced) =>
        new(provider, synced, [new LyricLine(TimeSpan.Zero, "la")]);

    private static readonly LyricsQuery Query = new("Song", "Band", Duration: TimeSpan.FromSeconds(200));

    [Fact]
    public async Task Synced_lyrics_win_over_an_earlier_providers_text()
    {
        var service = new LyricsService([new Stub("a", () => Lyrics("a", false)), new Stub("b", () => Lyrics("b", true))], NoLog.Instance);

        Assert.Equal("b", (await service.FindAsync(Query, CancellationToken.None))!.ProviderId);
    }

    [Fact]
    public async Task The_earlier_provider_wins_a_tie()
    {
        var service = new LyricsService([new Stub("a", () => Lyrics("a", true)), new Stub("b", () => Lyrics("b", true))], NoLog.Instance);

        Assert.Equal("a", (await service.FindAsync(Query, CancellationToken.None))!.ProviderId);
    }

    [Fact]
    public async Task Answers_are_remembered()
    {
        var stub = new Stub("a", () => null);
        var service = new LyricsService([stub], NoLog.Instance);

        Assert.Null(await service.FindAsync(Query, CancellationToken.None));
        Assert.Null(await service.FindAsync(Query, CancellationToken.None));
        Assert.Equal(1, stub.Calls);
    }

    [Fact]
    public async Task A_failed_lookup_is_retried()
    {
        bool down = true;
        var flaky = new Stub("a", () => down ? throw new HttpRequestException("503") : Lyrics("a", true));
        var log = new LinesLog();
        var service = new LyricsService([flaky], log);

        Assert.Null(await service.FindAsync(Query, CancellationToken.None));
        // Said in the Console, through the host's log (a module replacing it gets this too).
        Assert.Contains(log.Lines, l => l.StartsWith("lyrics: a unavailable", StringComparison.Ordinal));
        down = false;
        Assert.NotNull(await service.FindAsync(Query, CancellationToken.None));
    }
}
