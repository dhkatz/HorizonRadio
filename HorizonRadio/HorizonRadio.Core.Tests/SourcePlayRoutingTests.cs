using HorizonRadio.Core.Commands;
using HorizonRadio.Core.Events;
using HorizonRadio.Core.Playback;
using HorizonRadio.Core.Sources;
using HorizonRadio.Core.Sources.Config;
using HorizonRadio.Core.Sources.Mixes;
using HorizonRadio.Core.Sources.Queue;
using HorizonRadio.Sdk.Sources;

namespace HorizonRadio.Core.Tests;

/// <summary>
/// Content sources only ever play through the queue. The Sources tab's Play hands the
/// locator to <see cref="QueuePlayback.EnqueueLocatorAsync"/> with <c>playNow</c> (like
/// search's Play), and a "switch source" action can't start one on the runner directly.
/// </summary>
public class SourcePlayRoutingTests
{
    private sealed class NullSink : IPcmSink
    {
        public bool Send(ReadOnlySpan<short> samples) => true;
    }

    private static async Task WaitUntilAsync(Func<bool> condition, int timeoutMs = 5000)
    {
        var start = Environment.TickCount64;
        while (Environment.TickCount64 - start < timeoutMs)
        {
            if (condition()) return;
            await Task.Delay(40);
        }
        Assert.Fail("timed out waiting for the condition");
    }

    [Fact]
    public async Task Play_now_takes_over_from_what_is_playing_through_the_queue()
    {
        var config = new SourceConfigStore();
        await using var runner = new SourceRunner(new NullSink());
        await using var playback = new PlaybackRouter(runner, config, new MixStore(), new MixContentResolver(config, TestSources.Catalog));
        var queue = playback.Player;
        var titles = new List<string>();
        runner.TrackChanged += t =>
        {
            if (t.SourceId == "local") lock (titles) titles.Add(t.Title);
        };
        string? Last() { lock (titles) return titles.Count > 0 ? titles[^1] : null; }

        using var first = TempDir.Create();
        first.WriteSilentWav("first.wav", seconds: 5);
        using var second = TempDir.Create();
        second.WriteSilentWav("second.wav", seconds: 5);
        var local = TestSources.Catalog.Find("local")!;

        await queue.EnqueueLocatorAsync(local, first.Path);
        await WaitUntilAsync(() => Last() == "first");

        await queue.EnqueueLocatorAsync(local, second.Path, playNow: true);
        await WaitUntilAsync(() => Last() == "second");

        // The queue engine plays it — no per-source player, no factory on the runner.
        Assert.True(queue.IsActive);
        Assert.Null(runner.ActiveFactory);
    }

    [Fact]
    public async Task An_entry_is_queued_from_its_source_and_the_Console_says_so()
    {
        var config = new SourceConfigStore();
        var log = new LinesLog();
        await using var runner = new SourceRunner(new NullSink());
        await using var playback = new PlaybackRouter(runner, config, new MixStore(),
            new MixContentResolver(config, TestSources.Catalog), log: log);
        string? played = null;
        runner.TrackChanged += t => Volatile.Write(ref played, t.Title);
        using var dir = TempDir.Create();
        dir.WriteSilentWav("song.wav", seconds: 5);

        await playback.Player.EnqueueAsync(new ContentRef("local", dir.Path, "Song"));
        await WaitUntilAsync(() => Volatile.Read(ref played) == "song");
        Assert.Contains("queue: added: Song (local)", log.Lines);

        // One that can't be queued fails, and says why, for the caller to show.
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => playback.Player.EnqueueAsync(new ContentRef("nowhere", "x"), playNow: true));
        Assert.Contains(log.Lines, l => l.StartsWith("queue: couldn't play x (nowhere): ", StringComparison.Ordinal)
            && l.EndsWith(ex.Message, StringComparison.Ordinal));
    }

    private static PlaybackRouter Router(SourceRunner runner)
    {
        var config = new SourceConfigStore();
        return new PlaybackRouter(runner, config, new MixStore(), new MixContentResolver(config, TestSources.Catalog));
    }

    [Fact]
    public async Task Switch_source_action_ignores_a_content_source()
    {
        await using var runner = new SourceRunner(new NullSink());
        await using var playback = Router(runner);
        var dispatcher = new CommandList(playback, NoLog.Instance);

        await dispatcher.RunAsync(new EventAction(EventActionType.SwitchSource, "local"));

        Assert.False(runner.IsRunning);
    }

    [Fact]
    public async Task Switch_source_action_starts_a_self_driven_source()
    {
        await using var runner = new SourceRunner(new NullSink());
        await using var playback = Router(runner);
        var dispatcher = new CommandList(playback, NoLog.Instance);

        await dispatcher.RunAsync(new EventAction(EventActionType.SwitchSource, "testtone"));

        Assert.Equal("testtone", runner.ActiveFactory?.Id);
    }
}
