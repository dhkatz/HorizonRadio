using HorizonRadio.Core.Plugins;
using HorizonRadio.Core.Sources.Config;

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

    [Fact]
    public void Legacy_target_station_moves_into_the_FH6_plugin()
    {
        var plugins = PluginSettingsStore.CreateInMemory();
        var config = new SourceConfigStore { TargetStation = "Horizon Pulse" };

        Assert.True(LegacySettingsMigration.MoveTargetStation(config, plugins));
        Assert.Equal("Horizon Pulse", plugins.For("fh6").GetValue("station"));
        Assert.Null(config.TargetStation);

        // Nothing left to move.
        Assert.False(LegacySettingsMigration.MoveTargetStation(config, plugins));
    }

    [Fact]
    public void Legacy_migration_keeps_an_existing_plugin_value_and_drops_any_station()
    {
        var plugins = PluginSettingsStore.CreateInMemory();
        plugins.For("fh6").SetValue("station", "Horizon XS");

        Assert.True(LegacySettingsMigration.MoveTargetStation(new SourceConfigStore { TargetStation = "Horizon Pulse" }, plugins));
        Assert.Equal("Horizon XS", plugins.For("fh6").GetValue("station"));

        var fresh = PluginSettingsStore.CreateInMemory();
        Assert.True(LegacySettingsMigration.MoveTargetStation(new SourceConfigStore { TargetStation = "Any station" }, fresh));
        Assert.Null(fresh.For("fh6").GetValue("station"));
    }
}
