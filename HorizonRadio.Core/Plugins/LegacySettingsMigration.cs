using HorizonRadio.Core.Sources.Config;

namespace HorizonRadio.Core.Plugins;

/// <summary>
/// One-time moves of settings that predate game plugins into the plugin that now owns them.
/// </summary>
public static class LegacySettingsMigration
{
    /// <summary>The game plugin that owned station targeting before games were plugins.</summary>
    public const string LegacyStationGameId = "fh6";

    // The old player-bar picker persisted its "replace whatever's active" choice as this label.
    private const string LegacyAnyStationLabel = "Any station";

    /// <summary>
    /// Move the default target station from the app config (<c>targetStation</c>) into the FH6
    /// plugin's settings, unless the plugin already has one, then drop it from the app config.
    /// Returns true if the app config changed (the caller saves it).
    /// </summary>
    public static bool MoveTargetStation(SourceConfigStore config, PluginSettingsStore plugins)
    {
        if (config.TargetStation is not { } legacy) return false;

        var settings = plugins.For(LegacyStationGameId);
        if (settings.GetValue("station") == null && legacy != LegacyAnyStationLabel)
            settings.SetValue("station", legacy);

        config.TargetStation = null;
        return true;
    }
}
