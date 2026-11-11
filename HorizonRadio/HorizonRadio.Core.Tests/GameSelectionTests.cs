using HorizonRadio.Core.Games;
using HorizonRadio.Core.Plugins;
using HorizonRadio.Core.Settings;

namespace HorizonRadio.Core.Tests;

public class GameSelectionTests
{
    private static readonly string[] Installed = ["fh6"];

    [Fact]
    public void A_saved_choice_wins()
    {
        Assert.Equal("fh6", GameSelection.Resolve("fh6", Installed, []));
        Assert.Equal("none", GameSelection.Resolve("none", Installed, ["fh6"]));
    }

    [Fact]
    public void A_saved_game_that_is_no_longer_installed_asks_again()
    {
        Assert.Null(GameSelection.Resolve("fh5", Installed, []));
    }

    [Fact]
    public void Existing_users_keep_the_game_they_set_up()
    {
        Assert.Equal("fh6", GameSelection.Resolve(null, Installed, ["fh6"]));
        Assert.Null(GameSelection.Resolve(null, Installed, ["uninstalled"]));
    }

    [Fact]
    public void A_fresh_install_asks()
    {
        Assert.Null(GameSelection.Resolve(null, Installed, []));
    }

    [Fact]
    public void Plugin_settings_report_whether_a_plugin_has_been_set_up()
    {
        var store = SettingsStore.CreateInMemory();
        Assert.False(PluginSettings.HasSettings(store, "fh6"));
        PluginSettings.For(store, "fh6").SetValue("switchMode", "Live");
        Assert.True(PluginSettings.HasSettings(store, "fh6"));
    }

    [Fact]
    public async Task No_game_has_no_output_events_pages_or_capabilities()
    {
        await using var none = new NoGameIntegration();
        Assert.Null(none.Output);
        Assert.Empty(none.EventSources);
        Assert.Empty(none.EventCatalog);
        Assert.Empty(none.Pages);
        Assert.Null(none.Stations);
        Assert.Null(none.Volume);
        Assert.Null(none.NowPlaying);
        Assert.Null(none.Notice);
    }
}
