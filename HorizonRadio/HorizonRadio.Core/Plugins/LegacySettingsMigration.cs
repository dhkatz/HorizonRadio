using HorizonRadio.Core.Settings;
using HorizonRadio.Core.Sources.Mixes;

namespace HorizonRadio.Core.Plugins;

/// <summary>
/// One-time moves of settings that predate game plugins into the plugin that now owns them.
/// </summary>
public static class LegacySettingsMigration
{
    /// <summary>The game plugin that owned station targeting before games were plugins.</summary>
    public const string LegacyStationGameId = "fh6";

    /// <summary>LEGACY: the default target station from before games were plugins (<c>sources.json</c>
    /// <c>targetStation</c>). Only read, by <see cref="MigrateStations"/>, which then removes it.</summary>
    public static readonly Setting<string?> TargetStation = new("legacy.targetStation", SettingScope.Device, null);

    // FH6's station assignments (its Stations page): "station:<name>" → "player" | "mix:<id>".
    // Mirrors the FH6 plugin's own keys; HorizonRadio.Core.Tests checks the plugin reads them.
    private const string StationKeyPrefix = "station:";
    private const string PlayerAssignment = "player";
    private const string MixAssignmentPrefix = "mix:";

    // The old player-bar picker persisted its "replace whatever's active" choice as this label.
    private const string LegacyAnyStationLabel = "Any station";

    /// <summary>What a migration changed, so the caller saves the right files (settings save themselves).</summary>
    public readonly record struct Result(bool ConfigChanged, bool MixesChanged)
    {
        public bool Any => ConfigChanged || MixesChanged;
    }

    /// <summary>
    /// Move pre-plugin station settings into the FH6 plugin's station assignments: the default
    /// target station (<see cref="TargetStation"/>) now plays the app's player, and a mix's old
    /// station override now plays that mix. A station already assigned keeps its assignment, and
    /// "Any station" has no equivalent (every station keeps the game's music until the user assigns
    /// one). The legacy setting is removed; the caller saves the mixes if they changed, which drops
    /// their legacy fields, so this can't re-apply later.
    /// </summary>
    public static Result MigrateStations(SettingsStore store, MixStore mixes)
    {
        var settings = PluginSettings.For(store, LegacyStationGameId);

        void Assign(string station, string assignment)
        {
            if (settings.GetValue(StationKeyPrefix + station) == null)
                settings.SetValue(StationKeyPrefix + station, assignment);
        }

        var configChanged = false;
        if (store.Get(TargetStation) is { } target)
        {
            if (target != LegacyAnyStationLabel) Assign(target, PlayerAssignment);
            store.Reset(TargetStation);
            configChanged = true;
        }

        foreach (var (mixId, station) in mixes.LegacyStations)
        {
            if (mixes.Get(mixId) != null) Assign(station, MixAssignmentPrefix + mixId);
        }

        return new Result(configChanged, mixes.LegacyStations.Count > 0);
    }
}
