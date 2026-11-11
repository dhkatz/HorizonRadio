using System.Text.Json;
using HorizonRadio.Core.IO;
using HorizonRadio.Plugins.Abstractions;

namespace HorizonRadio.Core.Plugins;

/// <summary>
/// Persists every plugin's <see cref="IPluginSettings"/> in one hand-editable file,
/// <c>%LOCALAPPDATA%\HorizonRadio\plugin-settings.json</c>, keyed by plugin id:
/// <code>
/// { "plugins": { "fh6": { "station": "Horizon XS" } } }
/// </code>
/// Writes save immediately — plugin settings change on user action, rarely. Thread-safe.
/// </summary>
public sealed class PluginSettingsStore
{
    public static string DefaultPath => AppPaths.In("plugin-settings.json");

    private readonly string? _path; // null = in-memory only
    private readonly Lock _lock = new();
    private readonly Dictionary<string, Dictionary<string, string>> _plugins;

    private PluginSettingsStore(string? path, Dictionary<string, Dictionary<string, string>> plugins)
    {
        _path = path;
        _plugins = plugins;
    }

    /// <summary>Load from <paramref name="path"/> (default <see cref="DefaultPath"/>). A missing or
    /// unreadable file yields an empty store rather than failing startup.</summary>
    public static PluginSettingsStore LoadFromDisk(string? path = null)
    {
        path ??= DefaultPath;
        var plugins = new Dictionary<string, Dictionary<string, string>>(StringComparer.Ordinal);
        try
        {
            if (File.Exists(path))
            {
                using var doc = JsonDocument.Parse(File.ReadAllText(path));
                if (doc.RootElement.TryGetProperty("plugins", out var all) && all.ValueKind == JsonValueKind.Object)
                {
                    foreach (var plugin in all.EnumerateObject())
                    {
                        if (plugin.Value.ValueKind != JsonValueKind.Object) continue;
                        var values = new Dictionary<string, string>(StringComparer.Ordinal);
                        foreach (var kv in plugin.Value.EnumerateObject())
                        {
                            if (kv.Value.ValueKind == JsonValueKind.String)
                                values[kv.Name] = kv.Value.GetString()!;
                        }
                        plugins[plugin.Name] = values;
                    }
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            System.Diagnostics.Trace.WriteLine($"[hzn-plugins] settings load failed: {ex.Message}");
        }
        return new PluginSettingsStore(path, plugins);
    }

    /// <summary>A store that never touches disk (designer, tests).</summary>
    public static PluginSettingsStore CreateInMemory() => new(null, new(StringComparer.Ordinal));

    /// <summary>True if the plugin with <paramref name="pluginId"/> has stored any setting.</summary>
    public bool HasSettings(string pluginId)
    {
        lock (_lock) return _plugins.TryGetValue(pluginId, out var values) && values.Count > 0;
    }

    /// <summary>The settings view for one plugin.</summary>
    public IPluginSettings For(string pluginId) => new PluginSettings(this, pluginId);

    private string? Get(string pluginId, string key)
    {
        lock (_lock)
            return _plugins.TryGetValue(pluginId, out var values) && values.TryGetValue(key, out var v) ? v : null;
    }

    private void Set(string pluginId, string key, string? value)
    {
        lock (_lock)
        {
            if (!_plugins.TryGetValue(pluginId, out var values))
            {
                if (value == null) return;
                _plugins[pluginId] = values = new Dictionary<string, string>(StringComparer.Ordinal);
            }

            if (value == null)
            {
                if (!values.Remove(key)) return;
            }
            else
            {
                if (values.TryGetValue(key, out var old) && old == value) return;
                values[key] = value;
            }
            SaveLocked();
        }
    }

    private void SaveLocked()
    {
        if (_path == null) return;
        try
        {
            AtomicFile.Write(_path, stream =>
            {
                using var writer = new Utf8JsonWriter(stream, new JsonWriterOptions { Indented = true });
                writer.WriteStartObject();
                writer.WriteStartObject("plugins");
                foreach (var (pluginId, values) in _plugins.OrderBy(p => p.Key, StringComparer.Ordinal))
                {
                    writer.WriteStartObject(pluginId);
                    foreach (var (key, value) in values.OrderBy(v => v.Key, StringComparer.Ordinal))
                        writer.WriteString(key, value);
                    writer.WriteEndObject();
                }
                writer.WriteEndObject();
                writer.WriteEndObject();
            });
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            System.Diagnostics.Trace.WriteLine($"[hzn-plugins] settings save failed: {ex.Message}");
        }
    }

    private sealed class PluginSettings(PluginSettingsStore store, string pluginId) : IPluginSettings
    {
        public string? GetValue(string key) => store.Get(pluginId, key);

        public void SetValue(string key, string? value) => store.Set(pluginId, key, value);
    }
}
