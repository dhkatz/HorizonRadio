using HorizonRadio.Core.Party;
using HorizonRadio.Core.Playback;
using HorizonRadio.Core.Sources;
using HorizonRadio.Core.Sources.Config;
using HorizonRadio.Core.Sources.Mixes;
using HorizonRadio.Core.Sources.YouTube;
using HorizonRadio.Core.Tools;

namespace HorizonRadio.Core.Tests;

/// <summary>A guest's player telling a song it can't play apart from one it doesn't have: a source
/// here that has the song but lacks its tools says so, rather than "couldn't find".</summary>
[Collection("tool-resolver")]
public sealed class QueuePartyPlayerTests : IAsyncDisposable
{
    private readonly SourceRunner _runner = new(new NullPcmSink());
    private readonly PlaybackRouter _router;
    private readonly YouTubeSourceFactory _youTube = new();

    public QueuePartyPlayerTests()
    {
        var config = new SourceConfigStore();
        _router = new PlaybackRouter(_runner, config, new MixStore(), new MixContentResolver(config));
        ToolResolver.DiscoverOverride = _ => null; // a PC without yt-dlp or ffmpeg
    }

    public async ValueTask DisposeAsync()
    {
        ToolResolver.DiscoverOverride = null;
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
    public async Task A_song_nothing_here_has_is_not_found()
    {
        var song = Melt(new PartySource("spotify", "spotify:track:0"));
        Assert.False(await Player().PlayAsync(song, () => TimeSpan.Zero, CancellationToken.None));
    }
}
