using HorizonRadio.Core.Sources;
using HorizonRadio.Core.Sources.Config;
using HorizonRadio.Core.Sources.Mixes;

namespace HorizonRadio.Core.Tests;

/// <summary>
/// The mix context's sequencing: toggling shuffle mid-mix never replays the song
/// that's playing (nor, turning it on, songs already played this pass), and the
/// upcoming list reflects the new order as soon as the toggle lands.
/// </summary>
public class MixContextProviderTests
{
    private static MixContentResolver Resolver() => new(new SourceConfigStore());

    // A mix of single-track entries (one local file each): the common case.
    private static Mix SingleTrackMix(TempDir dir, params string[] names) =>
        new("m", "Mix", [.. names.Select(n => new ContentRef("local", dir.Touch($"{n}.mp3")))]);

    // A mix of one entry (a folder) holding several tracks.
    private static Mix FolderMix(TempDir dir, params string[] names)
    {
        foreach (var n in names) dir.Touch($"{n}.mp3");
        return new Mix("m", "Mix", [new ContentRef("local", dir.Path)]);
    }

    private static async Task<string> NextTitleAsync(MixContextProvider provider)
    {
        var item = await provider.NextAsync(CancellationToken.None);
        Assert.NotNull(item);
        return item.Metadata.Title;
    }

    private static async Task<List<string>> NextTitlesAsync(MixContextProvider provider, int count)
    {
        var titles = new List<string>();
        for (var i = 0; i < count; i++) titles.Add(await NextTitleAsync(provider));
        return titles;
    }

    [Fact]
    public async Task Shuffle_on_with_single_track_entries_does_not_replay_the_playing_song()
    {
        using var dir = TempDir.Create();
        var provider = new MixContextProvider(SingleTrackMix(dir, "a", "b", "c", "d"), Resolver());

        Assert.Equal("a", await NextTitleAsync(provider));
        provider.SetShuffle(true);

        var rest = await NextTitlesAsync(provider, 3);
        Assert.Equal(["b", "c", "d"], rest.Order());
    }

    [Fact]
    public async Task Shuffle_off_with_single_track_entries_continues_after_the_playing_song()
    {
        using var dir = TempDir.Create();
        string[] names = ["a", "b", "c", "d", "e"];
        var provider = new MixContextProvider(SingleTrackMix(dir, names), Resolver(), shuffle: true);

        var playing = await NextTitleAsync(provider);
        provider.SetShuffle(false);

        // Natural order resumes from the playing song.
        var expected = names[(Array.IndexOf(names, playing) + 1) % names.Length];
        Assert.Equal(expected, await NextTitleAsync(provider));
    }

    [Fact]
    public async Task Shuffle_on_mid_entry_keeps_played_songs_out_of_the_rest_of_the_pass()
    {
        using var dir = TempDir.Create();
        var provider = new MixContextProvider(FolderMix(dir, "a", "b", "c", "d", "e", "f"), Resolver());

        Assert.Equal(["a", "b"], await NextTitlesAsync(provider, 2));
        provider.SetShuffle(true);

        var rest = await NextTitlesAsync(provider, 4);
        Assert.Equal(["c", "d", "e", "f"], rest.Order());
    }

    [Fact]
    public async Task Shuffle_toggle_on_the_last_song_of_an_entry_moves_on_to_the_next_entry()
    {
        using var entryA = TempDir.Create();
        using var entryB = TempDir.Create();
        entryA.Touch("a1.mp3");
        entryA.Touch("a2.mp3");
        entryB.Touch("b1.mp3");
        var mix = new Mix("m", "Mix", [new ContentRef("local", entryA.Path), new ContentRef("local", entryB.Path)]);
        var provider = new MixContextProvider(mix, Resolver());

        Assert.Equal(["a1", "a2"], await NextTitlesAsync(provider, 2));
        provider.SetShuffle(true);

        Assert.Equal("b1", await NextTitleAsync(provider));
    }

    [Fact]
    public async Task Peek_reflects_a_shuffle_toggle_immediately()
    {
        using var dir = TempDir.Create();
        var provider = new MixContextProvider(FolderMix(dir, "a", "b", "c", "d", "e", "f"), Resolver());

        Assert.Equal("a", await NextTitleAsync(provider));
        Assert.Equal(["b", "c", "d", "e", "f"], provider.Peek(10).Select(p => p.Title));

        provider.SetShuffle(true);
        var upcoming = provider.Peek(10).Select(p => p.Title).ToList();
        Assert.Equal(["b", "c", "d", "e", "f"], upcoming.Order());
        Assert.Equal(upcoming, await NextTitlesAsync(provider, 5));
    }

    [Fact]
    public async Task Peek_excludes_the_playing_song()
    {
        using var dir = TempDir.Create();
        var provider = new MixContextProvider(FolderMix(dir, "a", "b", "c"), Resolver());

        Assert.Equal("a", await NextTitleAsync(provider));
        Assert.Equal(["b", "c"], provider.Peek(10).Select(p => p.Title));

        Assert.Equal(["b", "c"], await NextTitlesAsync(provider, 2));
        Assert.Empty(provider.Peek(10));
    }

    [Fact]
    public async Task Shuffled_mix_plays_every_entry_once_per_pass_and_reshuffles_on_wrap()
    {
        using var dir = TempDir.Create();
        string[] names = ["a", "b", "c", "d"];
        var provider = new MixContextProvider(SingleTrackMix(dir, names), Resolver(), shuffle: true);

        Assert.Equal(names, (await NextTitlesAsync(provider, 4)).Order());
        Assert.Equal(names, (await NextTitlesAsync(provider, 4)).Order());
    }

    [Fact]
    public async Task Sequential_mix_loops_in_order()
    {
        using var dir = TempDir.Create();
        var provider = new MixContextProvider(SingleTrackMix(dir, "a", "b", "c"), Resolver());

        Assert.Equal(["a", "b", "c", "a", "b"], await NextTitlesAsync(provider, 5));
    }
}
