using HorizonRadio.Core.Party;
using HorizonRadio.Core.Playback;
using HorizonRadio.Core.Sources;
using HorizonRadio.Core.Sources.Config;
using HorizonRadio.Core.Sources.Mixes;
using HorizonRadio.Core.Sources.Queue;

namespace HorizonRadio.Core.Tests;

/// <summary>What a party's host tells its guests about its player: the queue's song, where it is, and
/// whether it's paused, including while a song loads and between songs.</summary>
public sealed class PlayerPartySourceTests : IAsyncDisposable
{
    private readonly SourceRunner _runner = new(new NullPcmSink());
    private readonly PlaybackRouter _router;
    private readonly PlayerPartySource _host;

    public PlayerPartySourceTests()
    {
        var config = new SourceConfigStore();
        _router = new PlaybackRouter(_runner, config, new MixStore(), new MixContentResolver(config));
        _host = new PlayerPartySource(_runner, _router.Player);
    }

    public async ValueTask DisposeAsync()
    {
        _host.Dispose();
        await _router.DisposeAsync();
        await _runner.DisposeAsync();
    }

    private QueueModel Model => _router.Player.Model;

    [Fact]
    public async Task The_host_tells_its_song_where_it_is_and_nothing_between_songs()
    {
        var context = new FakeContext();
        var a = new FakeItem("a", TimeSpan.FromMinutes(3)) { At = TimeSpan.FromSeconds(30) };
        Model.AppendExplicit([a]);
        Model.SetContext(context, "m");
        await _router.Player.EnsureActiveAsync();
        await Eventually.Until(() => _host.Now is { Paused: false }, "a to play");

        var now = _host.Now!;
        Assert.Equal("a", now.Song.Title);
        Assert.Equal(TimeSpan.FromSeconds(30), now.Position);
        Assert.Equal(180_000, now.Song.DurationMs);
        var aKey = now.Song.Key;

        // Next: the context takes a while to hand over the next track. Meanwhile the queue still
        // shows a, but nothing is playing, so guests aren't told "a, paused".
        await _router.Player.ActiveEngine!.NextAsync();
        await Eventually.Until(() => context.Calls == 1, "the context to be asked");
        Assert.Same(a, Model.Current?.Item);
        Assert.Null(_host.Now);

        // The next one loads: it's the song now, paused until it starts, so guests wait for it.
        var b = new FakeItem("b", TimeSpan.FromMinutes(4)) { HoldStart = FakeItem.NewSignal() };
        context.Feed(b);
        await Eventually.Until(() => _host.Now is { Song.Title: "b" }, "b to be on");
        Assert.True(_host.Now!.Paused);
        Assert.Null(_host.Now.Position);
        Assert.NotEqual(aKey, _host.Now.Song.Key);

        b.HoldStart.TrySetResult();
        await Eventually.Until(() => _host.Now is { Song.Title: "b", Paused: false }, "b to start");
    }

    [Fact]
    public async Task The_queue_is_the_partys_and_a_guest_changes_it_as_the_host_can()
    {
        Model.AppendExplicit([new FakeItem("a"), new FakeItem("b"), new FakeItem("c")]);
        Model.SetContext(new FakeContext("Sam's Mix"), "m");
        var ids = Model.Snapshot().Explicit.Select(q => q.Id).ToList();
        Assert.Equal(["a", "b", "c"], _host.Queue.Songs.Select(s => s.Title));
        Assert.Equal(ids, _host.Queue.Songs.Select(s => s.Key));
        Assert.Equal("Sam's Mix", _host.Queue.From);

        await _host.ChangeQueueAsync(new PartyQueueChange { Op = PartyQueueChange.Remove, Id = ids[1] });
        await _host.ChangeQueueAsync(new PartyQueueChange { Op = PartyQueueChange.MoveTo, Id = ids[2], To = ids[0] });
        Assert.Equal(["c", "a"], _host.Queue.Songs.Select(s => s.Title));
        await _host.ChangeQueueAsync(new PartyQueueChange { Op = PartyQueueChange.Move, Id = ids[2], By = 1 });
        Assert.Equal(["a", "c"], _host.Queue.Songs.Select(s => s.Title));

        // A guest's song joins the queue as the guest told it, until it's found here.
        PartySource[] from = [new("youtube", "https://www.youtube.com/watch?v=nW6nGsEKDE0")];
        await _host.ChangeQueueAsync(new PartyQueueChange
        {
            Op = PartyQueueChange.Add,
            Songs = [new PartySong("", "Melt", "ryo", 263_000, false, from)],
        });
        var melt = _host.Queue.Songs.Single(s => s.Title == "Melt");
        Assert.Equal(from, melt.Sources);
        Assert.Equal(263_000, melt.DurationMs);
        Assert.Contains(melt.Key, Model.Snapshot().Explicit.Select(q => q.Id));

        await _host.ChangeQueueAsync(new PartyQueueChange { Op = PartyQueueChange.Clear });
        Assert.Empty(_host.Queue.Songs);
    }

    [Fact]
    public async Task A_pause_is_told()
    {
        var a = new FakeItem("a", TimeSpan.FromMinutes(3));
        Model.AppendExplicit([a]);
        await _router.Player.EnsureActiveAsync();
        await Eventually.Until(() => _host.Now is { Paused: false }, "a to play");
        var changed = 0;
        _host.Changed += () => Interlocked.Increment(ref changed);

        await _router.Player.ActiveEngine!.SetPausedAsync(true);
        Assert.True(_host.Now!.Paused);
        Assert.True(changed > 0);
    }
}
