using HorizonRadio.Core.Plugins;
using HorizonRadio.Core.Settings;
using HorizonRadio.Core.Sources.Mixes;
using HorizonRadio.Game.ForzaHorizon6;

namespace HorizonRadio.Core.Tests;

/// <summary>
/// Per-plugin persisted settings, and moving pre-plugin app settings into the plugin that owns them.
/// </summary>
public class PluginSettingsTests
{
    [Fact]
    public void Settings_persist_per_plugin()
    {
        using var dir = TempDir.Create();
        var path = Path.Combine(dir.Path, SettingsStore.FileName);

        using (var store = SettingsStore.Open(path))
        {
            PluginSettings.For(store, "fh6").SetValue("station", "Horizon XS");
            PluginSettings.For(store, "other").SetValue("station", "Elsewhere");
        }

        using (var reloaded = SettingsStore.Open(path))
        {
            Assert.Equal("Horizon XS", PluginSettings.For(reloaded, "fh6").GetValue("station"));
            Assert.Equal("Elsewhere", PluginSettings.For(reloaded, "other").GetValue("station"));
            Assert.Equal(SettingScope.Device, reloaded.ScopeOf(PluginSettings.KeyFor("fh6", "station")));

            // null removes the key.
            PluginSettings.For(reloaded, "fh6").SetValue("station", null);
        }
        using var again = SettingsStore.Open(path);
        Assert.Null(PluginSettings.For(again, "fh6").GetValue("station"));
        Assert.Equal("Elsewhere", PluginSettings.For(again, "other").GetValue("station"));
    }

    // Writes a pre-plugin mixes.json where mix "drive" overrode Horizon Pulse.
    private static MixStore LegacyMixes(TempDir dir)
    {
        var path = Path.Combine(dir.Path, "mixes.json");
        File.WriteAllText(path, """
            { "mixes": [ { "id": "drive", "name": "Drive", "station": "Horizon Pulse", "entries": [] } ] }
            """);
        return MixStore.LoadFromDisk(path);
    }

    // Checked through FH6's own StationMap, so the migration and the plugin agree on the keys.
    [Fact]
    public void Legacy_station_settings_become_FH6_station_assignments()
    {
        using var dir = TempDir.Create();
        var settings = SettingsStore.CreateInMemory();
        settings.Set(LegacySettingsMigration.TargetStation, "Horizon XS");

        var result = LegacySettingsMigration.MigrateStations(settings, LegacyMixes(dir));

        Assert.True(result.ConfigChanged);
        Assert.True(result.MixesChanged);
        Assert.False(settings.IsSet(LegacySettingsMigration.TargetStation.Key));
        var map = new StationMap(PluginSettings.For(settings, "fh6"));
        Assert.Equal(StationAssignment.Player, map.Get("Horizon XS"));               // the old default
        Assert.Equal(StationAssignment.ForMix("drive"), map.Get("Horizon Pulse"));  // the old override
        Assert.Equal(StationAssignment.Game, map.Get("Horizon Wave"));
    }

    [Fact]
    public void Legacy_migration_keeps_existing_assignments_and_drops_any_station()
    {
        using var dir = TempDir.Create();
        var settings = SettingsStore.CreateInMemory();
        new StationMap(PluginSettings.For(settings, "fh6")).Set("Horizon Pulse", StationAssignment.Player);
        settings.Set(LegacySettingsMigration.TargetStation, "Any station");

        LegacySettingsMigration.MigrateStations(settings, LegacyMixes(dir));

        var map = new StationMap(PluginSettings.For(settings, "fh6"));
        Assert.Equal(StationAssignment.Player, map.Get("Horizon Pulse")); // kept, not overwritten by the mix
        Assert.Equal(["Horizon Pulse"], map.TargetStations);              // "Any station" assigned nothing
    }

    [Fact]
    public void Nothing_to_migrate()
    {
        var result = LegacySettingsMigration.MigrateStations(SettingsStore.CreateInMemory(), new MixStore());
        Assert.False(result.Any);
    }
}
