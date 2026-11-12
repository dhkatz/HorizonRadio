using HorizonRadio.Core.Plugins;
using HorizonRadio.Core.Settings;
using HorizonRadio.Core.Sources;
using HorizonRadio.Game.ForzaHorizon6;
using HorizonRadio.Sdk.Games;
using Microsoft.Extensions.Time.Testing;

namespace HorizonRadio.Core.Tests;

/// <summary>A plugin's settings that follow the user to their other PCs: the keys it says are
/// preferences, such as FH6's station assignments.</summary>
public sealed class SyncedPluginSettingsTests
{
    private readonly FakeTimeProvider _clock = new(new DateTimeOffset(2026, 11, 10, 12, 0, 0, TimeSpan.Zero));

    private static bool Prefs(string key) => key.StartsWith("pref:", StringComparison.Ordinal);

    private static async Task Eventually(Func<bool> condition, string what)
    {
        var end = DateTime.UtcNow.AddSeconds(5);
        while (!condition())
        {
            if (DateTime.UtcNow > end) Assert.Fail($"timed out waiting for {what}");
            await Task.Delay(10);
        }
    }

    [Fact]
    public void Keys_the_plugin_names_are_synced_and_the_rest_stay_on_this_PC()
    {
        var store = SettingsStore.CreateInMemory(_clock);
        var settings = PluginSettings.For(store, "game", Prefs);

        settings.SetValue("pref:color", "blue");
        settings.SetValue("installPath", "D:/Games/Thing");

        Assert.Equal(SettingScope.Synced, store.ScopeOf(PluginSettings.KeyFor("game", "pref:color")));
        Assert.Equal(SettingScope.Device, store.ScopeOf(PluginSettings.KeyFor("game", "installPath")));
        Assert.Equal([PluginSettings.KeyFor("game", "pref:color")], store.SyncedSettings().Select(s => s.Key));
    }

    [Fact]
    public void A_key_stored_before_the_plugin_said_it_syncs_keeps_when_it_changed()
    {
        var store = SettingsStore.CreateInMemory(_clock);
        PluginSettings.For(store, "game").SetValue("pref:color", "blue"); // as an older build stored it
        var key = PluginSettings.KeyFor("game", "pref:color");
        var when = store.ModifiedAt(key);
        _clock.Advance(TimeSpan.FromHours(1));

        PluginSettings.For(store, "game", Prefs);

        Assert.Equal(SettingScope.Synced, store.ScopeOf(key));
        Assert.Equal(when, store.ModifiedAt(key)); // not a new change another PC would take over its own
    }

    [Fact]
    public void A_change_from_another_PC_is_raised_to_the_plugin()
    {
        var here = SettingsStore.CreateInMemory(_clock);
        var there = SettingsStore.CreateInMemory(_clock);
        var mine = PluginSettings.For(here, "game", Prefs);
        var changed = new List<string>();
        mine.Changed += changed.Add;

        _clock.Advance(TimeSpan.FromSeconds(1));
        PluginSettings.For(there, "game", Prefs).SetValue("pref:color", "green");
        here.MergeSynced(there.SyncedSettings());

        Assert.Equal("green", mine.GetValue("pref:color"));
        Assert.Equal(["pref:color"], changed);
    }

    [Fact]
    public async Task FH6_follows_station_assignments_made_on_another_PC()
    {
        var desktop = SettingsStore.CreateInMemory(_clock);
        var laptop = SettingsStore.CreateInMemory(_clock);
        var desktopMap = new StationMap(PluginSettings.For(desktop, "fh6", StationMap.IsSynced));
        var laptopMap = new StationMap(PluginSettings.For(laptop, "fh6", StationMap.IsSynced));
        var raised = 0;
        laptopMap.Changed += () => Interlocked.Increment(ref raised);
        var station = StationCatalog.Names[0];

        _clock.Advance(TimeSpan.FromSeconds(1));
        desktopMap.Set(station, StationAssignment.ForMix("drive"));
        desktopMap.Mode = StationSwitchMode.Live;
        laptop.MergeSynced(desktop.SyncedSettings());

        // The map takes the change on a pool thread and raises Changed after it, so wait for both.
        await Eventually(() => laptopMap.Get(station) == StationAssignment.ForMix("drive") && laptopMap.Mode == StationSwitchMode.Live
                && Volatile.Read(ref raised) > 0,
            "the laptop's map to take the desktop's assignment and mode, and say so");
    }

    [Fact]
    public async Task Two_PCs_that_each_put_the_player_on_a_station_end_up_with_one()
    {
        var desktop = SettingsStore.CreateInMemory(_clock);
        var laptop = SettingsStore.CreateInMemory(_clock);
        var desktopMap = new StationMap(PluginSettings.For(desktop, "fh6", StationMap.IsSynced));
        var laptopMap = new StationMap(PluginSettings.For(laptop, "fh6", StationMap.IsSynced));

        desktopMap.Set(StationCatalog.Names[0], StationAssignment.Player);
        laptopMap.Set(StationCatalog.Names[1], StationAssignment.Player);
        laptop.MergeSynced(desktop.SyncedSettings());

        await Eventually(() => StationCatalog.Names.Count(s => laptopMap.Get(s) == StationAssignment.Player) == 1
                && laptopMap.Get(StationCatalog.Names[0]) == StationAssignment.Player,
            "the laptop to keep one player station");
    }
}
