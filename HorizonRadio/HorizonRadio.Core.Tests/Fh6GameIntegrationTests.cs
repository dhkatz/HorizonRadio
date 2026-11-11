using System.IO.Pipes;
using System.Text.Json;
using HorizonRadio.Core.Events;
using HorizonRadio.Core.Games;
using HorizonRadio.Core.Metadata;
using HorizonRadio.Core.Models;
using HorizonRadio.Core.Plugins;
using HorizonRadio.Core.Settings;
using HorizonRadio.Core.Sources;
using HorizonRadio.Game.ForzaHorizon6;
using HorizonRadio.Plugins.Abstractions;

namespace HorizonRadio.Core.Tests;

// Mostly wiring, with no pipes or sockets opened. The reconnect test is the exception: it runs the
// integration against a fake mod on pipe names of its own.
public class Fh6GameIntegrationTests
{
    private sealed class TestContext(SettingsStore settings) : IPluginContext
    {
        public IMetadataCache Cache => throw new NotSupportedException();

        public IPluginSettings GetSettings(string pluginId) => PluginSettings.For(settings, pluginId);
    }

    [Fact]
    public async Task Plugin_builds_an_integration_with_every_FH6_capability()
    {
        var plugin = new Fh6GamePlugin();
        Assert.Equal("fh6", plugin.Id);

        await using var game = plugin.Create(new TestContext(SettingsStore.CreateInMemory()));
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
        var settings = PluginSettings.For(SettingsStore.CreateInMemory(), "fh6");
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
        var store = SettingsStore.CreateInMemory();
        new StationMap(PluginSettings.For(store, "fh6")).Set("Horizon XS", StationAssignment.Player);

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

    private sealed class NullRouter : IPlaybackRouter
    {
        public IReadOnlyList<MixInfo> Mixes => [];
        public event Action? MixesChanged { add { } remove { } }
        public void Route(PlaybackRoute route) { }
    }

    private sealed class Host : IGameHost
    {
        public IPlaybackRouter Playback { get; } = new NullRouter();
    }

    /// <summary>Stands in for the mod's control pipe: accepts the app, then reads its commands up
    /// to and including the first <c>set_gain</c> (the last thing the app re-sends on connect).
    /// Disposing it is the game going away.</summary>
    private static async Task<(NamedPipeServerStream Pipe, List<JsonElement> Commands)> AcceptAsync(
        string pipeName, CancellationToken ct)
    {
        var pipe = new NamedPipeServerStream(pipeName, PipeDirection.InOut, 1, PipeTransmissionMode.Byte,
            PipeOptions.Asynchronous);
        await pipe.WaitForConnectionAsync(ct);
        var reader = new StreamReader(pipe, leaveOpen: true);
        var commands = new List<JsonElement>();
        while (await reader.ReadLineAsync(ct) is { } line)
        {
            var cmd = JsonDocument.Parse(line).RootElement.Clone();
            commands.Add(cmd);
            if (cmd.GetProperty("cmd").GetString() == "set_gain") break;
        }
        return (pipe, commands);
    }

    /// <summary>Closes the fake mod's pipe and waits for the integration to notice. It retries a
    /// second later, so whatever the test does right after happens while the game is gone.</summary>
    private static async Task GameGoneAsync(Fh6GameIntegration game, NamedPipeServerStream pipe,
        CancellationToken ct)
    {
        var gone = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        void OnState(ConnectionState s)
        {
            if (s == ConnectionState.Disconnected) gone.TrySetResult();
        }
        game.StateChanged += OnState;
        await pipe.DisposeAsync();
        await gone.Task.WaitAsync(ct);
        game.StateChanged -= OnState;
    }

    private static string[] Names(List<JsonElement> commands) =>
        [.. commands.Select(c => c.GetProperty("cmd").GetString()!)];

    // The mod forgets the app's track and duck when the app leaves, and a restarted game starts
    // without them, so every (re)connect puts back what the app last asked for, even what it asked
    // while the game was gone.
    [Fact]
    public async Task Reconnecting_resends_the_last_track_and_duck_gain()
    {
        var pipeName = $"HorizonRadioTest-{Guid.NewGuid():N}";
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        await using var game = new Fh6GameIntegration("Forza Horizon 6", null, pipeName, pipeName + ".pcm",
            telemetryPort: 0);
        var display = game.NowPlaying!;
        var volume = game.Volume!;

        // Asked before the game is up: nothing to send to yet, but remembered.
        Assert.False(display.Show(new Track("Melt", "ryo", null, null, "test", "Test")));
        Assert.False(volume.SetDuckGain(0.25f));

        var accepted = AcceptAsync(pipeName, cts.Token);
        game.Start(new Host());
        var (pipe, commands) = await accepted;
        Assert.Equal(["set_target_stations", "set_track", "set_gain"], Names(commands));
        Assert.Equal("Melt", commands[1].GetProperty("title").GetString());
        Assert.Equal(0.25, commands[2].GetProperty("gain").GetDouble(), 3);

        // The game restarts; the song changes while it's gone.
        await GameGoneAsync(game, pipe, cts.Token);
        Assert.False(display.Show(new Track("World is Mine", "ryo", null, null, "test", "Test")));
        (pipe, commands) = await AcceptAsync(pipeName, cts.Token);
        Assert.Equal(["set_target_stations", "set_track", "set_gain"], Names(commands));
        Assert.Equal("World is Mine", commands[1].GetProperty("title").GetString());
        Assert.Equal(0.25, commands[2].GetProperty("gain").GetDouble(), 3);

        // Nothing playing any more, and the duck lifted: that's what comes back, not the old song.
        await GameGoneAsync(game, pipe, cts.Token);
        Assert.False(display.Clear());
        Assert.False(volume.SetDuckGain(1f));
        (pipe, commands) = await AcceptAsync(pipeName, cts.Token);
        Assert.Equal(["set_target_stations", "clear_track", "set_gain"], Names(commands));
        Assert.Equal(1.0, commands[2].GetProperty("gain").GetDouble(), 3);
        await pipe.DisposeAsync();
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
