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
/// The commands a key, an event rule or a remote caller runs by name: every saved action has one,
/// and each says what it did or why it couldn't.
/// </summary>
public sealed class CommandListTests
{
    [Fact]
    public void Every_saved_action_runs_a_command_with_a_name_of_its_own()
    {
        foreach (var type in Enum.GetValues<EventActionType>().Where(t => t != EventActionType.None))
            Assert.Equal(type, CommandList.For(type).Action);

        Assert.Equal(CommandList.BuiltIn.Count, CommandList.BuiltIn.Select(c => c.Name).Distinct().Count());
        Assert.Equal(CommandList.BuiltIn.Count, CommandList.BuiltIn.Select(c => c.Action).Distinct().Count());
        Assert.Same(CommandList.For(EventActionType.NextTrack), CommandList.Find("Playback.Next"));
    }

    [Fact]
    public async Task Next_by_name_skips_to_the_next_song()
    {
        var a = new FakeItem("a");
        var b = new FakeItem("b");
        var model = new QueueModel();
        model.AppendExplicit([a, b]);
        await using var runner = new SourceRunner(new NullPcmSink());
        await using var router = Router(runner, new MixStore());
        await runner.StartSourceAsync(new QueueSource(model));
        await Eventually.Until(() => a.Plays == 1, "a");

        var result = await new CommandList(router, NoLog.Instance).RunAsync("playback.next");

        Assert.True(result.Done);
        await Eventually.Until(() => b.Plays == 1, "b");
    }

    [Fact]
    public async Task What_can_not_be_done_is_said_and_logged()
    {
        var log = new LinesLog();
        await using var runner = new SourceRunner(new NullPcmSink());
        await using var router = Router(runner, new MixStore());
        var commands = new CommandList(router, log);

        var unknown = await commands.RunAsync("playback.rewind");
        var nothingPlaying = await commands.RunAsync("playback.next");
        var noMixes = await commands.RunAsync("mix.next");
        var noVolume = await commands.RunAsync("volume.set", "0.3");

        Assert.Equal("no command 'playback.rewind'", unknown.Message);
        Assert.All([unknown, nothingPlaying, noMixes, noVolume], r => Assert.False(r.Done));
        Assert.Equal(4, log.Lines.Count(l => l.StartsWith($"{CommandList.ConsoleTag}: ", StringComparison.Ordinal)));
    }

    [Fact]
    public async Task A_mix_that_is_not_saved_is_not_switched_to()
    {
        var mixes = new MixStore();
        await using var runner = new SourceRunner(new NullPcmSink());
        await using var router = Router(runner, mixes);
        using var switcher = new MixSwitcher(mixes, router.Player, runner);
        var commands = new CommandList(router, NoLog.Instance, switcher, mixes);

        var result = await commands.RunAsync("mix.switch", "gone");

        Assert.False(result.Done);
        Assert.Equal("switch mix: unknown mix 'gone'", result.Message);
    }

    [Fact]
    public async Task Volume_sets_the_game_gain_and_is_described_in_percent()
    {
        var gains = new List<float>();
        var mixes = new MixStore();
        mixes.AddOrUpdate(new Mix("drive", "Drive", [new ContentRef("local", "x")]));
        await using var runner = new SourceRunner(new NullPcmSink());
        await using var router = Router(runner, mixes);
        var commands = new CommandList(router, NoLog.Instance, null, mixes, g => { gains.Add(g); return true; });

        var result = await commands.RunAsync("volume.set", "1.5");

        Assert.True(result.Done);
        Assert.Equal([1f], gains); // clamped to full
        Assert.Equal("Volume 30%", commands.Describe(new EventAction(EventActionType.SetVolume, "0.3")));
        Assert.Equal("Mix: Drive", commands.Describe(new EventAction(EventActionType.SwitchMix, "drive")));
        Assert.Equal("Next Track", commands.Describe(new EventAction(EventActionType.NextTrack)));
    }

    private static PlaybackRouter Router(SourceRunner runner, MixStore mixes)
    {
        var config = new SourceConfigStore();
        return new PlaybackRouter(runner, config, mixes, new MixContentResolver(config, TestSources.Catalog));
    }
}
