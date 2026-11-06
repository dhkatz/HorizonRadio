using HorizonRadio.Core.Models;
using HorizonRadio.Core.Sources;
using HorizonRadio.Game.ForzaHorizon6;

namespace HorizonRadio.Core.Tests;

// Wiring only: nothing here calls Start(), so no pipes or sockets are opened.
public class Fh6GameIntegrationTests
{
    [Fact]
    public async Task Plugin_builds_an_integration_with_every_FH6_capability()
    {
        var plugin = new Fh6GamePlugin();
        Assert.Equal("fh6", plugin.Id);

        await using var game = plugin.Create(context: null!);
        Assert.Equal("Forza Horizon 6", game.DisplayName);
        Assert.NotNull(game.Output);
        Assert.NotNull(game.Stations);
        Assert.NotNull(game.Volume);
        Assert.NotNull(game.NowPlaying);
        // The DLL's memory poller + Forza Data Out telemetry.
        Assert.Equal(2, game.EventSources.Count);
        Assert.Equal(StationCatalog.Names, game.Stations!.Stations);
    }

    [Fact]
    public async Task Starts_disconnected_and_commands_fail_soft_before_connecting()
    {
        await using var game = new Fh6GameIntegration("Forza Horizon 6");
        Assert.Equal(ConnectionState.Disconnected, game.State);

        // No DLL to talk to: best-effort commands report false instead of throwing.
        Assert.False(game.Stations!.SetTarget(null));
        Assert.False(game.Volume!.SetMasterVolume(0.5f));
        Assert.False(game.NowPlaying!.Show(new Track("Title", "Artist", null, null, "test", "Test")));
        Assert.False(game.Output!.Send([0, 0]));
    }

    [Fact]
    public async Task Contributes_Mods_and_Stats_pages_and_a_telemetry_hint()
    {
        await using var game = new Fh6GameIntegration("Forza Horizon 6");

        Assert.Contains("127.0.0.1:9967", game.EventsHint);
        Assert.Collection(game.Pages,
            mods => Assert.Equal(("mods", "Mods"), (mods.Id, mods.Title)),
            stats => Assert.Equal(("stats", "Stats"), (stats.Id, stats.Title)));
        // Built once: the shell holds on to these view models.
        Assert.Same(game.Pages, game.Pages);
        // Each page view model has a view next to it, by the ViewLocator's naming convention.
        foreach (var page in game.Pages)
        {
            var vm = page.ViewModel.GetType();
            Assert.NotNull(vm.Assembly.GetType(vm.FullName!.Replace("ViewModel", "View", StringComparison.Ordinal)));
        }
        // A notice, when present, must point at a page that exists.
        if (game.Notice is { } notice)
            Assert.Contains(game.Pages, p => p.Id == notice.PageId);
    }
}
