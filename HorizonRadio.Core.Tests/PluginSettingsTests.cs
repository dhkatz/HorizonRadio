using HorizonRadio.Core.Plugins;
using HorizonRadio.Core.Sources.Config;
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
        var path = Path.Combine(dir.Path, "plugin-settings.json");

        var store = PluginSettingsStore.LoadFromDisk(path);
        store.For("fh6").SetValue("station", "Horizon XS");
        store.For("other").SetValue("station", "Elsewhere");

        var reloaded = PluginSettingsStore.LoadFromDisk(path);
        Assert.Equal("Horizon XS", reloaded.For("fh6").GetValue("station"));
        Assert.Equal("Elsewhere", reloaded.For("other").GetValue("station"));

        // null removes the key.
        reloaded.For("fh6").SetValue("station", null);
        Assert.Null(PluginSettingsStore.LoadFromDisk(path).For("fh6").GetValue("station"));
    }

    [Fact]
    public void Missing_or_corrupt_file_yields_empty_settings()
    {
        using var dir = TempDir.Create();
        var path = Path.Combine(dir.Path, "plugin-settings.json");
        Assert.Null(PluginSettingsStore.LoadFromDisk(path).For("fh6").GetValue("station"));

        File.WriteAllText(path, "{ not json");
        Assert.Null(PluginSettingsStore.LoadFromDisk(path).For("fh6").GetValue("station"));
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
        var plugins = PluginSettingsStore.CreateInMemory();
        var config = new SourceConfigStore { TargetStation = "Horizon XS" };

        var result = LegacySettingsMigration.MigrateStations(config, LegacyMixes(dir), plugins);

        Assert.True(result.ConfigChanged);
        Assert.True(result.MixesChanged);
        Assert.Null(config.TargetStation);
        var map = new StationMap(plugins.For("fh6"));
        Assert.Equal(StationAssignment.Player, map.Get("Horizon XS"));               // the old default
        Assert.Equal(StationAssignment.ForMix("drive"), map.Get("Horizon Pulse"));  // the old override
        Assert.Equal(StationAssignment.Game, map.Get("Horizon Wave"));
    }

    [Fact]
    public void Legacy_migration_keeps_existing_assignments_and_drops_any_station()
    {
        using var dir = TempDir.Create();
        var plugins = PluginSettingsStore.CreateInMemory();
        new StationMap(plugins.For("fh6")).Set("Horizon Pulse", StationAssignment.Player);

        LegacySettingsMigration.MigrateStations(new SourceConfigStore { TargetStation = "Any station" }, LegacyMixes(dir), plugins);

        var map = new StationMap(plugins.For("fh6"));
        Assert.Equal(StationAssignment.Player, map.Get("Horizon Pulse")); // kept, not overwritten by the mix
        Assert.Equal(["Horizon Pulse"], map.TargetStations);              // "Any station" assigned nothing
    }

    [Fact]
    public void Nothing_to_migrate()
    {
        var result = LegacySettingsMigration.MigrateStations(
            new SourceConfigStore(), new MixStore(), PluginSettingsStore.CreateInMemory());
        Assert.False(result.Any);
    }
}
