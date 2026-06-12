using HorizonRadio.Core.Sources;
using HorizonRadio.Core.Sources.Config;
using HorizonRadio.Core.Sources.Mixes;

namespace HorizonRadio.Core.Tests;

/// <summary>
/// The mix launch path: the switcher resolves a mix, starts it as a MixSource on
/// the runner, tracks the current mix, and rejects unknown/empty mixes.
/// </summary>
public class MixSwitcherTests
{
    private sealed class NullSink : IPcmSink
    {
        public bool Send(ReadOnlySpan<short> samples) => true;
    }

    [Fact]
    public async Task Switch_to_local_mix_starts_a_mix_source()
    {
        using var dir = TempDir.Create();
        dir.WriteSilentWav("a.wav", seconds: 0.1);

        var store = new MixStore();
        store.AddOrUpdate(new Mix("m", "M", [new ContentRef("local", dir.Path)]));

        var runner = new SourceRunner(new NullSink());
        using var switcher = new MixSwitcher(store, new SourceConfigStore(), runner);

        await switcher.SwitchToAsync("m");

        Assert.True(runner.IsRunning);
        Assert.Equal("mix", runner.ActiveSource!.Id);
        Assert.Null(runner.ActiveFactory); // a mix has no single factory
        Assert.Equal("m", switcher.CurrentMixId);

        await runner.StopAsync();
    }

    [Fact]
    public async Task Switch_to_unknown_mix_throws()
    {
        var runner = new SourceRunner(new NullSink());
        using var switcher = new MixSwitcher(new MixStore(), new SourceConfigStore(), runner);
        await Assert.ThrowsAsync<InvalidOperationException>(() => switcher.SwitchToAsync("nope"));
    }

    [Fact]
    public async Task Switch_to_empty_mix_throws()
    {
        var store = new MixStore();
        store.AddOrUpdate(new Mix("e", "Empty", []));
        var runner = new SourceRunner(new NullSink());
        using var switcher = new MixSwitcher(store, new SourceConfigStore(), runner);
        await Assert.ThrowsAsync<InvalidOperationException>(() => switcher.SwitchToAsync("e"));
    }
}
