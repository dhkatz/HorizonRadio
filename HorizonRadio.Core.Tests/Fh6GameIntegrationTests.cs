using HorizonRadio.Core.Events;
using HorizonRadio.Core.Games;
using HorizonRadio.Core.Metadata;
using HorizonRadio.Core.Models;
using HorizonRadio.Core.Plugins;
using HorizonRadio.Core.Sources;
using HorizonRadio.Game.ForzaHorizon6;
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
        Assert.Null(game.Stations.CurrentStation); // not connected
    }

    [Fact]
    public async Task Starts_disconnected_and_commands_fail_soft_before_connecting()
    {
        await using var game = new Fh6GameIntegration("Forza Horizon 6");
        Assert.Equal(ConnectionState.Disconnected, game.State);

        // No DLL to talk to: best-effort commands report false instead of throwing.
        Assert.False(game.Volume!.SetMasterVolume(0.5f));
        Assert.False(game.NowPlaying!.Show(new Track("Title", "Artist", null, null, "test", "Test")));
        Assert.False(game.Output!.Send([0, 0]));
    }

    [Fact]
    public async Task Contributes_Mods_Stations_and_Stats_pages_and_a_telemetry_hint()
    {
        await using var game = new Fh6GameIntegration("Forza Horizon 6");

        Assert.Contains("127.0.0.1:9967", game.EventsHint);
        Assert.Equal(["mods", "stations", "stats"], game.Pages.Select(p => p.Id));
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

    [Fact]
    public void Stations_play_the_game_by_default_and_route_by_assignment()
    {
        var map = new StationMap(null);
        Assert.False(map.HasAssignments);
        Assert.Empty(map.TargetStations);

        // Unknown station (not connected): plain player playback.
        Assert.Equal(PlaybackRoute.Player, map.RouteFor(null));
        // Unassigned station: the game's own music.
        Assert.Equal(PlaybackRoute.Silent, map.RouteFor("Horizon XS"));

        map.Set("Horizon XS", StationAssignment.Player);
        map.Set("Horizon Pulse", StationAssignment.ForMix("drive"));
        Assert.Equal(PlaybackRoute.Player, map.RouteFor("Horizon XS"));
        Assert.Equal(PlaybackRoute.Mix("drive", StationSwitchMode.Resume), map.RouteFor("Horizon Pulse"));
        Assert.Equal(["Horizon Pulse", "Horizon XS"], map.TargetStations);

        map.Mode = StationSwitchMode.Live;
        Assert.Equal(PlaybackRoute.Mix("drive", StationSwitchMode.Live), map.RouteFor("Horizon Pulse"));
    }

    [Fact]
    public void Only_one_station_plays_the_player()
    {
        var map = new StationMap(null);
        map.Set("Horizon XS", StationAssignment.Player);
        map.Set("Horizon Wave", StationAssignment.Player);

        Assert.Equal(StationAssignment.Game, map.Get("Horizon XS"));
        Assert.Equal(StationAssignment.Player, map.Get("Horizon Wave"));
    }

    [Fact]
    public void Assignments_and_mode_persist_and_ignore_unknown_stations()
    {
        var store = PluginSettingsStore.CreateInMemory();
        var settings = store.For("fh6");
        var map = new StationMap(settings);
        map.Set("Horizon XS", StationAssignment.ForMix("drive"));
        map.Mode = StationSwitchMode.Restart;
        map.Set("Not A Station", StationAssignment.Player); // ignored

        var reloaded = new StationMap(settings);
        Assert.Equal(StationAssignment.ForMix("drive"), reloaded.Get("Horizon XS"));
        Assert.Equal(StationSwitchMode.Restart, reloaded.Mode);
        Assert.Equal(["Horizon XS"], reloaded.TargetStations);
    }

    [Fact]
    public void Stations_playing_a_deleted_mix_go_back_to_the_game()
    {
        var map = new StationMap(null);
        map.Set("Horizon XS", StationAssignment.ForMix("gone"));
        map.Set("Horizon Pulse", StationAssignment.ForMix("kept"));
        var changes = 0;
        map.Changed += () => changes++;

        map.ForgetMissingMixes(["kept"]);

        Assert.Equal(StationAssignment.Game, map.Get("Horizon XS"));
        Assert.Equal(StationAssignment.ForMix("kept"), map.Get("Horizon Pulse"));
        Assert.Equal(1, changes);
    }

    [Fact]
    public async Task Integration_reads_its_station_map_from_the_plugin_settings()
    {
        var store = PluginSettingsStore.CreateInMemory();
        new StationMap(store.For("fh6")).Set("Horizon XS", StationAssignment.Player);

        await using var game = new Fh6GamePlugin().Create(new TestContext(store));
        var fh6 = Assert.IsType<Fh6GameIntegration>(game);
        Assert.Equal(StationAssignment.Player, fh6.Map.Get("Horizon XS"));
    }

    [Fact]
    public async Task Reports_its_events_and_describes_what_a_station_plays()
    {
        await using var game = new Fh6GameIntegration("Forza Horizon 6");

        Assert.Equal(GameEventKinds.Catalog, game.EventCatalog);
        Assert.Equal("stations", game.Stations!.PageId);
        Assert.Null(game.Stations.CurrentAssignment); // nothing tuned (not connected)

        Assert.Equal("Horizon Radio player", game.Describe(StationAssignment.Player));
        Assert.Equal("Game audio", game.Describe(StationAssignment.Game));
        Assert.Equal("Mix", game.Describe(StationAssignment.ForMix("not-loaded"))); // no host mixes yet
    }

    // The host slots its Events page in by order, between the game's own pages.
    [Fact]
    public async Task Page_order_leaves_room_for_the_host_events_page()
    {
        await using var game = new Fh6GameIntegration("Forza Horizon 6");
        var orders = game.Pages.ToDictionary(p => p.Id, p => p.Order);

        Assert.True(orders["stations"] < GamePage.EventsOrder);
        Assert.True(orders["stats"] > GamePage.EventsOrder);
    }
}
