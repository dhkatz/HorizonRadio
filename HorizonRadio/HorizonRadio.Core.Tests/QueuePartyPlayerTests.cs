using HorizonRadio.Core.Party;
using HorizonRadio.Core.Playback;
using HorizonRadio.Core.Sources;
using HorizonRadio.Core.Sources.Config;
using HorizonRadio.Core.Sources.Mixes;
using HorizonRadio.Sdk.Sources;
using HorizonRadio.Source.YouTube;

namespace HorizonRadio.Core.Tests;

/// <summary>A guest's player telling a song it can't play apart from one it doesn't have: a source
/// here that has the song but lacks its tools says so, rather than "couldn't find".</summary>
public sealed class QueuePartyPlayerTests : IAsyncDisposable
{
    private readonly SourceRunner _runner = new(new NullPcmSink());
    private readonly PlaybackRouter _router;
    private readonly YouTubeSourceFactory _youTube = TestSources.YouTube;

    public QueuePartyPlayerTests()
    {
        var config = new SourceConfigStore();
        _router = new PlaybackRouter(_runner, config, new MixStore(), new MixContentResolver(config, TestSources.Catalog));
    }

    public async ValueTask DisposeAsync()
    {
        await _router.DisposeAsync();
        await _runner.DisposeAsync();
    }

    private static PartySong Melt(params PartySource[] sources) =>
        new("k1", "Melt", "ryo", 263_000, false, sources);

    private QueuePartyPlayer Player(params PartySource[] found) =>
        new(_router.Player, (_, _) => Task.FromResult<IReadOnlyList<PartySource>>(found),
            id => id == _youTube.Id ? _youTube : null);

    [Fact]
    public async Task A_song_on_a_source_without_its_tools_says_which_tools()
    {
        var song = Melt(new PartySource(_youTube.Id, "https://www.youtube.com/watch?v=nW6nGsEKDE0"));
        var ex = await Assert.ThrowsAsync<PartyException>(() => Player().PlayAsync(song, () => TimeSpan.Zero, CancellationToken.None));
        Assert.Equal("Can't play \"Melt\" here: YouTube needs yt-dlp and ffmpeg. Install them from the Tools tab.", ex.Message);
    }

    [Fact]
    public async Task The_hosts_queue_is_shown_and_its_next_song_found_ahead_then_played_as_found()
    {
        var items = new Dictionary<string, FakeItem> { ["loc1"] = new("Melt"), ["loc2"] = new("World is Mine") };
        var finds = new Dictionary<string, int>();
        var finder = new PartySongFinder((_, locator, _) =>
            {
                lock (finds) finds[locator] = finds.GetValueOrDefault(locator) + 1;
                return Task.FromResult<PlayableItem>(items[locator]);
            },
            (_, _) => Task.FromResult<IReadOnlyList<PartySource>>([]), id => id == _youTube.Id ? _youTube : null);
        var player = new QueuePartyPlayer(_router.Player, finder);
        var model = _router.Player.Model;
        PartySong Song(string key, string title, string locator) => new(key, title, "ryo", 240_000, false, [new PartySource(_youTube.Id, locator)]);

        player.ShareQueue(new RecordingQueueOwner());
        player.MirrorQueue(new PartyQueueList([Song("k2", "World is Mine", "loc2")], null, []));
        Assert.Equal(["k2"], model.Snapshot().Explicit.Select(q => q.Id));
        Assert.Equal("World is Mine", model.Snapshot().Explicit[0].Metadata.Title);

        Assert.True(await player.PlayAsync(Song("k1", "Melt", "loc1"), () => TimeSpan.Zero, CancellationToken.None));
        await Eventually.Until(() => items["loc1"].Plays == 1, "Melt to play");
        await Eventually.Until(() => items["loc2"].Prefetches > 0, "the next song to be found and loaded ahead");

        // The host takes its next song off its list as it starts it; then the guest hears it's on.
        player.MirrorQueue(PartyQueueList.Empty);
        Assert.True(await player.PlayAsync(Song("k2", "World is Mine", "loc2"), () => TimeSpan.Zero, CancellationToken.None));
        await Eventually.Until(() => items["loc2"].Plays == 1, "World is Mine to play");
        Assert.Equal("k2", model.Current?.Id);
        lock (finds) Assert.Equal(1, finds["loc2"]); // found once, ahead

        player.UnshareQueue();
        Assert.Null(model.Owner);
    }

    [Fact]
    public async Task A_song_nothing_here_has_is_not_found()
    {
        var song = Melt(new PartySource("spotify", "spotify:track:0"));
        Assert.False(await Player().PlayAsync(song, () => TimeSpan.Zero, CancellationToken.None));
    }
}
