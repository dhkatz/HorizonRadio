using HorizonRadio.Core.Settings;
using HorizonRadio.Plugins.Abstractions;

namespace HorizonRadio.Core.Plugins;

/// <summary>
/// A plugin's own settings (<see cref="IPluginSettings"/>), kept in the <see cref="SettingsStore"/>
/// as <c>plugin.&lt;plugin id&gt;.&lt;key&gt;</c>, text values. Their meaning is the plugin's
/// business, so they stay on this machine (<see cref="SettingScope.Device"/>) unless the plugin says
/// a key is a preference that follows the user (<c>synced</c>): then it's a
/// <see cref="SettingScope.Synced"/> setting.
/// </summary>
public static class PluginSettings
{
    private const string Prefix = "plugin.";

    /// <summary>The scope of a key the plugin hasn't said is synced.</summary>
    public const SettingScope Scope = SettingScope.Device;

    /// <summary>The setting a plugin's key is kept under.</summary>
    public static string KeyFor(string pluginId, string key) => $"{Prefix}{pluginId}.{key}";

    /// <summary>The settings view for one plugin; the keys <paramref name="synced"/> says yes to are
    /// synced (one already stored keeps its value and when it last changed).</summary>
    public static IPluginSettings For(SettingsStore store, string pluginId, Func<string, bool>? synced = null)
    {
        var view = new View(store, pluginId, synced ?? (_ => false));
        var prefix = KeyFor(pluginId, "");
        foreach (var key in store.KeysWithPrefix(prefix))
        {
            if (view.IsSynced(key[prefix.Length..])) store.Rescope(key, SettingScope.Synced);
        }
        return view;
    }

    /// <summary>True if the plugin with <paramref name="pluginId"/> has stored any setting.</summary>
    public static bool HasSettings(SettingsStore store, string pluginId) =>
        store.KeysWithPrefix(KeyFor(pluginId, "")).Count > 0;

    private sealed class View : IPluginSettings
    {
        private readonly SettingsStore _store;
        private readonly string _pluginId;
        private readonly Func<string, bool> _synced;

        public View(SettingsStore store, string pluginId, Func<string, bool> synced)
        {
            _store = store;
            _pluginId = pluginId;
            _synced = synced;
            var prefix = KeyFor(pluginId, "");
            store.Changed += key =>
            {
                if (key.StartsWith(prefix, StringComparison.Ordinal)) Changed?.Invoke(key[prefix.Length..]);
            };
        }

        public event Action<string>? Changed;

        public bool IsSynced(string key) => _synced(key);

        public string? GetValue(string key) =>
            _store.TryGetRaw(KeyFor(_pluginId, key), out var value) && value.ValueKind == System.Text.Json.JsonValueKind.String
                ? value.GetString()
                : null;

        public void SetValue(string key, string? value)
        {
            if (value == null) _store.Remove(KeyFor(_pluginId, key));
            else _store.SetRaw(KeyFor(_pluginId, key), System.Text.Json.JsonSerializer.SerializeToElement(value), _synced(key) ? SettingScope.Synced : Scope);
        }
    }
}
