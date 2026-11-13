using HorizonRadio.Core.Events;
using HorizonRadio.Core.Playback;
using HorizonRadio.Core.Sources;
using HorizonRadio.Core.Sources.Config;
using HorizonRadio.Core.Sources.Mixes;
using HorizonRadio.Core.Sources.Queue;
using HorizonRadio.Sdk.Models;
using HorizonRadio.Sdk.Sources;

namespace HorizonRadio.Core.Tests;

/// <summary>
/// End-to-end checks of the queue engine over real local items: it plays explicit
/// items before the context, pumps each item's PCM, publishes a track per item, and
/// the mix context refills the tail forever (loops past one lap). When the explicit
/// zone drains with no context it parks idle rather than ending.
/// </summary>
public class QueueSourceTests
{
    private sealed class CountingSink : IPcmSink
    {
        private long _samples;
        public long Samples => Interlocked.Read(ref _samples);
        public bool Send(ReadOnlySpan<short> samples)
        {
            Interlocked.Add(ref _samples, samples.Length);
            return true;
        }
    }

    private static MixContentResolver Resolver() => new(new SourceConfigStore(), TestSources.Catalog);

    // Bound actions reach the engine through the router; these tests put a queue engine of
    // their own on its runner.
    private static PlaybackRouter Router(SourceRunner runner) =>
        new(runner, new SourceConfigStore(), new MixStore(), Resolver());

    private static async Task<IReadOnlyList<PlayableItem>> ResolveAsync(string locator)
        => await Resolver().EnumerateAsync(new ContentRef("local", locator), CancellationToken.None);

    private static int CountLocal(List<Track> tracks)
    {
        lock (tracks) return tracks.Count(t => t.SourceId == "local");
    }

    private static async Task WaitUntilAsync(Func<bool> condition, int timeoutMs = 5000)
    {
        var start = Environment.TickCount64;
        while (Environment.TickCount64 - start < timeoutMs)
        {
            if (condition()) return;
            await Task.Delay(40);
        }
    }

    [Fact]
    public async Task Context_plays_entry_items_and_publishes_tracks()
    {
        using var dir = TempDir.Create();
        dir.WriteSilentWav("a.wav", seconds: 0.15);
        dir.WriteSilentWav("b.wav", seconds: 0.15);

        var mix = new Mix("t", "Test", [new ContentRef("local", dir.Path)]);
        var model = new QueueModel();
        model.SetContext(new MixContextProvider(mix, Resolver()), "t");

        await using var src = new QueueSource(model);
        var tracks = new List<Track>();
        src.TrackChanged += t => { lock (tracks) tracks.Add(t); };

        var sink = new CountingSink();
        using var cts = new CancellationTokenSource();
        await src.StartAsync(sink, cts.Token);

        await WaitUntilAsync(() => CountLocal(tracks) >= 2);
        await src.StopAsync();

        Assert.True(sink.Samples > 0, "expected PCM to be pumped");
        Assert.True(CountLocal(tracks) >= 2, $"expected >=2 local tracks, got {CountLocal(tracks)}");
    }

    [Fact]
    public async Task Explicit_items_play_before_the_context()
    {
        using var dir = TempDir.Create();
        var x = dir.WriteSilentWav("xfirst.wav", seconds: 0.12);
        using var ctxDir = TempDir.Create();
        ctxDir.WriteSilentWav("ylater.wav", seconds: 0.12);

        var model = new QueueModel();
        model.AppendExplicit(await ResolveAsync(x));
        var mix = new Mix("c", "Ctx", [new ContentRef("local", ctxDir.Path)]);
        model.SetContext(new MixContextProvider(mix, Resolver()), "c");

        await using var src = new QueueSource(model);
        var titles = new List<string>();
        src.TrackChanged += t =>
        {
            if (t.SourceId != "local") return;
            lock (titles) if (titles.Count == 0 || titles[^1] != t.Title) titles.Add(t.Title);
        };

        using var cts = new CancellationTokenSource();
        await src.StartAsync(new CountingSink(), cts.Token);

        await WaitUntilAsync(() =>
        {
            lock (titles) return titles.Contains("xfirst") && titles.Contains("ylater");
        });
        await src.StopAsync();

        lock (titles)
        {
            var xi = titles.IndexOf("xfirst");
            var yi = titles.IndexOf("ylater");
            Assert.True(xi >= 0 && yi >= 0, $"both should play; saw [{string.Join(",", titles)}]");
            Assert.True(xi < yi, "the explicit item must play before the context");
        }
    }

    [Fact]
    public async Task Context_refills_past_one_lap()
    {
        using var dir = TempDir.Create();
        dir.WriteSilentWav("a.wav", seconds: 0.1);
        dir.WriteSilentWav("b.wav", seconds: 0.1);

        var mix = new Mix("t", "Loop", [new ContentRef("local", dir.Path)]);
        var model = new QueueModel();
        model.SetContext(new MixContextProvider(mix, Resolver()), "t");

        await using var src = new QueueSource(model);
        var count = 0;
        src.TrackChanged += t => { if (t.SourceId == "local") Interlocked.Increment(ref count); };

        using var cts = new CancellationTokenSource();
        await src.StartAsync(new CountingSink(), cts.Token);

        // More than the two items proves the entry order wrapped and replayed.
        await WaitUntilAsync(() => Volatile.Read(ref count) >= 4, timeoutMs: 8000);
        await src.StopAsync();

        Assert.True(Volatile.Read(ref count) >= 4, $"expected the context to loop; got {count} tracks");
    }

    [Fact]
    public async Task Drains_to_idle_when_explicit_empties_with_no_context()
    {
        using var dir = TempDir.Create();
        var only = dir.WriteSilentWav("only.wav", seconds: 0.12);

        var model = new QueueModel();
        model.AppendExplicit(await ResolveAsync(only));

        await using var src = new QueueSource(model);
        var localTracks = 0;
        src.TrackChanged += t => { if (t.SourceId == "local") Interlocked.Increment(ref localTracks); };

        using var cts = new CancellationTokenSource();
        await src.StartAsync(new CountingSink(), cts.Token);

        await WaitUntilAsync(() => Volatile.Read(ref localTracks) >= 1);
        // Let the single item finish and the engine park.
        await Task.Delay(600);

        Assert.Equal(1, Volatile.Read(ref localTracks)); // nothing further played
        Assert.False(src.CanSkipNext, "with nothing queued and no context, next is disabled");

        await src.StopAsync();
    }

    // An item that pumps until it's cancelled (a long track or a station), counting its plays.
    private sealed class EndlessItem : PlayableItem
    {
        private int _plays;
        private readonly TaskCompletionSource _ended = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public int Plays => Volatile.Read(ref _plays);
        public Task Ended => _ended.Task;

        public EndlessItem() => Metadata = new Track("Endless", "", null, null, "test", "Test");

        public override async Task PlayAsync(PumpContext ctx, CancellationToken ct)
        {
            Interlocked.Increment(ref _plays);
            ctx.OnStarted?.Invoke(this);
            try { await Task.Delay(Timeout.Infinite, ct); }
            finally { _ended.TrySetResult(); }
        }
    }

    [Fact]
    public async Task A_parked_source_plays_again_when_its_track_ended_before_the_stop()
    {
        var item = new EndlessItem();
        var model = new QueueModel();
        model.AppendExplicit([item]);

        await using var src = new QueueSource(model);
        using var cts = new CancellationTokenSource();
        await src.StartAsync(new CountingSink(), cts.Token);
        await WaitUntilAsync(() => item.Plays == 1);
        Assert.Equal(1, item.Plays);

        // Park it the way SourceRunner does: cancel the owner's token, then stop the source. The
        // engine winds the track down on its own thread, so it can finish (and drop the track's
        // cancellation source) before the stop runs; here it always does. The engine notes where
        // it was itself.
        await cts.CancelAsync();
        await item.Ended.WaitAsync(TimeSpan.FromSeconds(5));
        await Task.Delay(100);
        // SourceRunner swallows a failed stop, so check the source comes back regardless.
        var stopError = await Record.ExceptionAsync(src.StopAsync);

        src.ResumeFromPark(TimeSpan.Zero);
        await src.StartAsync(new CountingSink(), CancellationToken.None);
        await WaitUntilAsync(() => item.Plays == 2);

        Assert.Null(stopError);
        Assert.Equal(2, item.Plays);
    }

    // -- Requests, driven step by step with fakes --

    private static async Task<QueueSource> StartedAsync(QueueModel model)
    {
        var src = new QueueSource(model);
        await src.StartAsync(new NullPcmSink(), CancellationToken.None);
        return src;
    }

    [Fact]
    public async Task The_next_track_is_loaded_ahead_while_one_plays_and_follows_queue_changes()
    {
        var (a, b, c) = (new FakeItem("a"), new FakeItem("b"), new FakeItem("c"));
        var model = new QueueModel();
        model.AppendExplicit([a, b]);
        await using var src = new QueueSource(model) { PrefetchAfter = TimeSpan.FromMilliseconds(50) };
        await src.StartAsync(new NullPcmSink(), CancellationToken.None);
        await Eventually.Until(() => a.Plays == 1, "a to play");
        await Eventually.Until(() => b.Prefetches > 0, "b to be loaded ahead");
        Assert.Equal(0, a.Prefetches);

        // b leaves the queue: c is next now.
        model.AppendExplicit([c]);
        model.RemoveExplicit(model.Snapshot().Explicit[0].Id);
        await Eventually.Until(() => c.Prefetches > 0, "c to be loaded ahead");
    }

    [Fact]
    public async Task The_contexts_next_track_is_loaded_ahead()
    {
        var context = new FakeContext();
        var (a, next) = (new FakeItem("a"), new FakeItem("next"));
        var model = new QueueModel();
        model.SetContext(context, "m");
        await using var src = new QueueSource(model) { PrefetchAfter = TimeSpan.FromMilliseconds(50) };
        await src.StartAsync(new NullPcmSink(), CancellationToken.None);
        context.Ahead = next;
        context.Feed(a);
        await Eventually.Until(() => next.Prefetches > 0, "the context's next track to be loaded ahead");
    }

    [Fact]
    public async Task Play_now_while_the_context_is_still_resolving_plays_at_once()
    {
        var context = new FakeContext();
        var model = new QueueModel();
        model.SetContext(context, "m");
        await using var src = await StartedAsync(model);
        await Eventually.Until(() => context.Calls == 1, "the context to be asked for a track");

        var now = new FakeItem("now");
        model.JumpToExplicit(model.AppendExplicit([now])[0]);

        await Eventually.Until(() => now.Plays == 1, "the play-now item to start");
        Assert.Equal(1, context.Cancelled); // the resolve it was waiting on is dropped
    }

    [Fact]
    public async Task Play_next_plays_at_once_from_where_it_joins_and_keeps_the_queue()
    {
        var first = new FakeItem("first");
        var later = new FakeItem("later");
        var model = new QueueModel();
        model.AppendExplicit([first, later]);
        await using var src = await StartedAsync(model);
        await Eventually.Until(() => first.Plays == 1, "the first track");

        // A party's song: where it starts is worked out as it starts, after it has loaded.
        var joining = new FakeItem("party") { HoldStart = FakeItem.NewSignal() };
        var hostAt = TimeSpan.FromSeconds(30);
        model.PlayNext(joining, () => hostAt);
        await Eventually.Until(() => joining.Plays == 1, "the party's song to start loading");
        hostAt = TimeSpan.FromSeconds(33); // the host played on while it loaded
        joining.HoldStart.TrySetResult();
        await Eventually.Until(() => joining.StartPositions.Count == 1, "it to start");
        Assert.Equal(TimeSpan.FromSeconds(33), joining.StartPositions[0]);
        Assert.Equal(["later"], model.Snapshot().Explicit.Select(q => q.Metadata.Title)); // still queued

        await src.RestartAsync(); // a restart is from the top
        await Eventually.Until(() => joining.StartPositions.Count == 2, "the restart");
        Assert.Equal(TimeSpan.Zero, joining.StartPositions[1]);
    }

    [Fact]
    public async Task A_mix_switch_while_the_context_is_still_resolving_takes_effect_at_once()
    {
        var oldMix = new FakeContext("old");
        var newMix = new FakeContext("new");
        var model = new QueueModel();
        model.SetContext(oldMix, "old");
        await using var src = await StartedAsync(model);
        await Eventually.Until(() => oldMix.Calls == 1, "the old mix to be asked for a track");

        var first = new FakeItem("new-1");
        newMix.Feed(first);
        model.SetContext(newMix, "new", replaceNow: true);

        await Eventually.Until(() => first.Plays == 1, "the new mix to play");
        Assert.Equal(1, oldMix.Cancelled);
    }

    [Fact]
    public async Task A_track_the_old_context_resolves_after_a_switch_never_plays()
    {
        var oldMix = new FakeContext("old");
        var newMix = new FakeContext("new");
        var model = new QueueModel();
        model.SetContext(oldMix, "old");
        await using var src = await StartedAsync(model);
        await Eventually.Until(() => oldMix.Calls == 1, "the old mix to be asked for a track");

        var fresh = new FakeItem("new-1");
        newMix.Feed(fresh);
        model.SetContext(newMix, "new"); // nothing playing to interrupt
        var stale = new FakeItem("old-1");
        oldMix.Feed(stale); // the old mix's resolve finishes after the switch

        await Eventually.Until(() => fresh.Plays == 1, "the new mix to play");
        Assert.Equal(0, stale.Plays);
    }

    [Fact]
    public async Task A_second_skip_while_the_next_track_loads_skips_that_one_too()
    {
        var context = new FakeContext();
        var first = new FakeItem("first");
        var model = new QueueModel();
        model.AppendExplicit([first]);
        model.SetContext(context, "m");
        await using var src = await StartedAsync(model);
        await Eventually.Until(() => first.Plays == 1, "the first track");

        await src.NextAsync(); // the next comes from the context, which takes a while
        await Eventually.Until(() => context.Calls == 1, "the context to be asked for a track");
        await src.NextAsync(); // skip that one too, before it has even arrived

        var skipped = new FakeItem("skipped");
        context.Feed(skipped);
        await Eventually.Until(() => context.Calls == 2, "the context to be asked again");
        var played = new FakeItem("played");
        context.Feed(played);

        await Eventually.Until(() => played.Plays == 1, "the track after the skipped one");
        Assert.Equal(0, skipped.Plays);
    }

    [Fact]
    public async Task Play_now_after_stepping_back_plays_the_new_item_not_the_one_stepped_over()
    {
        var a = new FakeItem("a");
        var b = new FakeItem("b");
        var model = new QueueModel();
        model.AppendExplicit([a, b]);
        await using var src = await StartedAsync(model);
        await Eventually.Until(() => a.Plays == 1, "a");
        await src.NextAsync();
        await Eventually.Until(() => b.Plays == 1, "b");
        await src.PreviousAsync(); // back to a; b waits to be redone
        await Eventually.Until(() => a.Plays == 2, "a again");

        var now = new FakeItem("now");
        model.JumpToExplicit(model.AppendExplicit([now])[0]);

        await Eventually.Until(() => now.Plays == 1, "the play-now item");
        Assert.Equal(1, b.Plays);
        Assert.False(src.CanSkipNext); // the step back is forgotten: nothing left to redo
    }

    [Fact]
    public async Task Next_works_as_soon_as_something_is_queued()
    {
        var a = new FakeItem("a");
        var model = new QueueModel();
        model.AppendExplicit([a]);
        await using var runner = new SourceRunner(new NullPcmSink());
        await using var playback = Router(runner);
        var src = new QueueSource(model);
        await runner.StartSourceAsync(src);
        await Eventually.Until(() => a.Plays == 1, "a");
        Assert.False(src.CanSkipNext);

        var b = new FakeItem("b");
        model.AppendExplicit([b]);

        Assert.True(src.CanSkipNext);
        // A bound Next (hotkey, game event) goes through the same check.
        await new ActionDispatcher(playback, new SourceConfigStore(), NoLog.Instance).RunAsync(new EventAction(EventActionType.NextTrack));
        await Eventually.Until(() => b.Plays == 1, "b");
    }

    [Fact]
    public async Task Next_right_after_restart_skips_instead_of_restarting()
    {
        var release = FakeItem.NewSignal();
        var a = new FakeItem("a") { HoldCancel = release };
        var b = new FakeItem("b");
        var model = new QueueModel();
        model.AppendExplicit([a, b]);
        await using var src = await StartedAsync(model);
        await Eventually.Until(() => a.Plays == 1, "a");

        // Both land before the engine has reacted to the first.
        await src.RestartAsync();
        await src.NextAsync();
        release.TrySetResult();

        await Eventually.Until(() => b.Plays == 1, "b");
        Assert.Equal(1, a.Plays);
    }

    [Fact]
    public async Task Pause_toggled_from_several_threads_at_once_stays_consistent()
    {
        var a = new FakeItem("a");
        var model = new QueueModel();
        model.AppendExplicit([a]);
        await using var src = await StartedAsync(model);
        await Eventually.Until(() => a.Plays == 1, "a");
        var pump = a.Context!;

        for (var round = 0; round < 20; round++)
        {
            using var go = new Barrier(4);
            await Task.WhenAll(Enumerable.Range(0, 4).Select(_ => Task.Run(async () =>
            {
                go.SignalAndWait();
                for (var i = 0; i < 1000; i++) await src.TogglePauseAsync();
            })));

            // An even number of toggles lands back where it started, and the pump's gate agrees:
            // never "playing" with the gate shut (silence), nor "paused" with it open.
            Assert.False(src.IsPaused, $"paused after an even number of toggles (round {round})");
            Assert.Equal(pump.IsPaused(), !pump.ResumeGate.IsSet);
        }
    }

    [Fact]
    public async Task Pause_and_resume_actions_set_the_state_rather_than_toggle_it()
    {
        var a = new FakeItem("a");
        var model = new QueueModel();
        model.AppendExplicit([a]);
        await using var runner = new SourceRunner(new NullPcmSink());
        await using var playback = Router(runner);
        var src = new QueueSource(model);
        await runner.StartSourceAsync(src);
        await Eventually.Until(() => a.Plays == 1, "a");
        var dispatcher = new ActionDispatcher(playback, new SourceConfigStore(), NoLog.Instance);

        await dispatcher.RunAsync(new EventAction(EventActionType.Pause));
        await dispatcher.RunAsync(new EventAction(EventActionType.Pause));
        Assert.True(src.IsPaused);

        await Task.WhenAll(Enumerable.Range(0, 8).Select(_ =>
            Task.Run(() => dispatcher.RunAsync(new EventAction(EventActionType.Resume)))));
        Assert.False(src.IsPaused);
        Assert.True(a.Context!.ResumeGate.IsSet);
    }
}
