using HorizonRadio.Core.Settings;
using HorizonRadio.Plugins.Abstractions;

namespace HorizonRadio.Core.Plugins;

/// <summary>
/// A plugin's own settings (<see cref="IPluginSettings"/>), kept in the <see cref="SettingsStore"/>
/// as <c>plugin.&lt;plugin id&gt;.&lt;key&gt;</c>, text values. Their meaning is the plugin's
/// business, so the host can't tell whether one is safe to sync: they stay on this machine
/// (<see cref="SettingScope.Device"/>).
/// </summary>
public static class PluginSettings
{
    private const string Prefix = "plugin.";

    public const SettingScope Scope = SettingScope.Device;

    /// <summary>The setting a plugin's key is kept under.</summary>
    public static string KeyFor(string pluginId, string key) => $"{Prefix}{pluginId}.{key}";

    /// <summary>The settings view for one plugin.</summary>
    public static IPluginSettings For(SettingsStore store, string pluginId) => new View(store, pluginId);

    /// <summary>True if the plugin with <paramref name="pluginId"/> has stored any setting.</summary>
    public static bool HasSettings(SettingsStore store, string pluginId) =>
        store.KeysWithPrefix(KeyFor(pluginId, "")).Count > 0;

    private sealed class View(SettingsStore store, string pluginId) : IPluginSettings
    {
        public string? GetValue(string key) =>
            store.TryGetRaw(KeyFor(pluginId, key), out var value) && value.ValueKind == System.Text.Json.JsonValueKind.String
                ? value.GetString()
                : null;

        public void SetValue(string key, string? value)
        {
            if (value == null) store.Remove(KeyFor(pluginId, key));
            else store.SetRaw(KeyFor(pluginId, key), System.Text.Json.JsonSerializer.SerializeToElement(value), Scope);
        }
    }
}
