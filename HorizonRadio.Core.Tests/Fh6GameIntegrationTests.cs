using HorizonRadio.Core.Games;
using HorizonRadio.Core.Metadata;
using HorizonRadio.Core.Models;
using HorizonRadio.Core.Plugins;
using HorizonRadio.Core.Sources;
using HorizonRadio.Game.ForzaHorizon6;
using HorizonRadio.Game.ForzaHorizon6.ViewModels;
using HorizonRadio.Plugins.Abstractions;

namespace HorizonRadio.Core.Tests;

// Wiring only: nothing here calls Start(), so no pipes or sockets are opened.
public class Fh6GameIntegrationTests
{
    private sealed class TestContext(PluginSettingsStore settings) : IPluginContext
    {
        public IMetadataCache Cache => throw new NotSupportedException();

        public IPluginSettings GetSettings(string pluginId) => settings.For(pluginId);
    }

    private static Dictionary<string, string> StationSetting(string station) => new() { ["station"] = station };

    [Fact]
    public async Task Plugin_builds_an_integration_with_every_FH6_capability()
    {
        var plugin = new Fh6GamePlugin();
        Assert.Equal("fh6", plugin.Id);

        await using var game = plugin.Create(new TestContext(PluginSettingsStore.CreateInMemory()));
        Assert.Equal("Forza Horizon 6", game.DisplayName);
        Assert.NotNull(game.Output);
        Assert.NotNull(game.Stations);
        Assert.NotNull(game.Volume);
        Assert.NotNull(game.NowPlaying);
        // The DLL's memory poller + Forza Data Out telemetry.
        Assert.Equal(2, game.EventSources.Count);
        Assert.Equal(StationCatalog.Names, game.Stations!.Stations);
        Assert.NotNull(game.MixSettings);
    }

    [Fact]
    public async Task Default_station_persists_in_the_plugin_settings()
    {
        var store = PluginSettingsStore.CreateInMemory();
        var plugin = new Fh6GamePlugin();

        await using (var game = plugin.Create(new TestContext(store)))
            game.Stations!.DefaultStation = "Horizon XS";
        Assert.Equal("Horizon XS", store.For("fh6").GetValue("station"));

        await using (var reloaded = plugin.Create(new TestContext(store)))
            Assert.Equal("Horizon XS", reloaded.Stations!.DefaultStation);

        // An unknown name (stale config, renamed station) means "whatever is active".
        store.For("fh6").SetValue("station", "Not A Station");
        await using (var stale = plugin.Create(new TestContext(store)))
            Assert.Null(stale.Stations!.DefaultStation);
    }

    [Fact]
    public async Task Active_mix_override_wins_over_the_default()
    {
        await using var game = new Fh6GameIntegration("Forza Horizon 6");
        var stations = game.Stations!;
        var mixes = game.MixSettings!;
        var changes = 0;
        stations.Changed += () => changes++;

        stations.DefaultStation = "Horizon XS";
        Assert.Equal("Horizon XS", stations.EffectiveStation);

        mixes.ApplyMix(StationSetting("Horizon Pulse"));
        Assert.Equal("Horizon Pulse", stations.EffectiveStation);
        Assert.Equal("Horizon XS", stations.DefaultStation); // the default is untouched

        // A mix without an override (or no mix at all) falls back to the default.
        mixes.ApplyMix(null);
        Assert.Equal("Horizon XS", stations.EffectiveStation);

        // Re-applying the same state isn't a change.
        mixes.ApplyMix(null);
        stations.DefaultStation = "Horizon XS";
        Assert.Equal(3, changes);
    }

    [Fact]
    public async Task Mix_editor_round_trips_the_station_override()
    {
        await using var game = new Fh6GameIntegration("Forza Horizon 6");
        var mixes = game.MixSettings!;

        var editor = mixes.CreateEditor(StationSetting("Horizon Wave"));
        var vm = Assert.IsType<MixSettingsViewModel>(editor.ViewModel);
        Assert.Equal("Horizon Wave", vm.SelectedStation);
        Assert.Equal("Horizon Wave", editor.Settings!["station"]);

        vm.SelectedStation = MixSettingsViewModel.UseDefault;
        Assert.Null(editor.Settings);

        Assert.Equal(MixSettingsViewModel.UseDefault, Assert.IsType<MixSettingsViewModel>(mixes.CreateEditor(null).ViewModel).SelectedStation);
        Assert.Equal("Station: Horizon Wave", mixes.Describe(StationSetting("Horizon Wave")));
        Assert.Null(mixes.Describe(null));
    }

    [Fact]
    public async Task Starts_disconnected_and_commands_fail_soft_before_connecting()
    {
        await using var game = new Fh6GameIntegration("Forza Horizon 6");
        Assert.Equal(ConnectionState.Disconnected, game.State);

        // No DLL to talk to: best-effort commands report false (or no-op) instead of throwing.
        game.Stations!.DefaultStation = "Horizon XS";
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
