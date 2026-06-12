using HorizonRadio.Core.Sources;
using HorizonRadio.Core.Sources.Config;
using HorizonRadio.Core.Sources.Mixes;
using HorizonRadio.Core.Sources.Queue;

namespace HorizonRadio.Core.Tests;

/// <summary>
/// The mix launch path: the switcher resolves a mix and makes it the queue's
/// context via <see cref="QueuePlayback"/>, the queue engine becomes the active
/// source, the current mix is tracked, and unknown/empty mixes are rejected.
/// </summary>
public class MixSwitcherTests
{
    private sealed class NullSink : IPcmSink
    {
        public bool Send(ReadOnlySpan<short> samples) => true;
    }

    private static (MixSwitcher switcher, SourceRunner runner, QueuePlayback queue) Build(MixStore store)
    {
        var runner = new SourceRunner(new NullSink());
        var config = new SourceConfigStore();
        var queue = new QueuePlayback(runner, config, new MixContentResolver(config));
        var switcher = new MixSwitcher(store, queue, runner);
        return (switcher, runner, queue);
    }

    [Fact]
    public async Task Switch_to_local_mix_makes_the_queue_the_active_source()
    {
        using var dir = TempDir.Create();
        dir.WriteSilentWav("a.wav", seconds: 0.1);

        var store = new MixStore();
        store.AddOrUpdate(new Mix("m", "M", [new ContentRef("local", dir.Path)]));

        var (switcher, runner, queue) = Build(store);
        using var _ = switcher;
        using var __ = queue;

        await switcher.SwitchToAsync("m");

        Assert.True(runner.IsRunning);
        Assert.Equal("queue", runner.ActiveSource!.Id);
        Assert.Null(runner.ActiveFactory);     // the queue has no single factory
        Assert.Equal("m", switcher.CurrentMixId);
        Assert.Equal("m", queue.Model.ContextMixId);

        await runner.StopAsync();
    }

    [Fact]
    public async Task Switch_to_unknown_mix_throws()
    {
        var (switcher, _, queue) = Build(new MixStore());
        using var _s = switcher;
        using var _q = queue;
        await Assert.ThrowsAsync<InvalidOperationException>(() => switcher.SwitchToAsync("nope"));
    }

    [Fact]
    public async Task Switch_to_empty_mix_throws()
    {
        var store = new MixStore();
        store.AddOrUpdate(new Mix("e", "Empty", []));
        var (switcher, _, queue) = Build(store);
        using var _s = switcher;
        using var _q = queue;
        await Assert.ThrowsAsync<InvalidOperationException>(() => switcher.SwitchToAsync("e"));
    }
}
