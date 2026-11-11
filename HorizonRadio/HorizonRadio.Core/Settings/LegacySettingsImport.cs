using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text.Json;
using HorizonRadio.Core.Audio;
using HorizonRadio.Core.Diagnostics;
using HorizonRadio.Core.Games;
using HorizonRadio.Core.IO;
using HorizonRadio.Core.Lyrics;
using HorizonRadio.Core.Playback;
using HorizonRadio.Core.Plugins;
using HorizonRadio.Core.Sources;
using HorizonRadio.Core.Sources.Config;

namespace HorizonRadio.Core.Settings;

/// <summary>
/// Brings the settings kept in per-feature files before <see cref="SettingsStore"/> into it, once
/// (when <c>settings.json</c> doesn't exist yet):
/// <list type="bullet">
/// <item><c>sources.json</c>: the app-level preferences, and each source's form
/// (<c>perSource</c> → <c>source.&lt;id&gt;.&lt;key&gt;</c>, its encrypted secrets as they are).</item>
/// <item><c>plugin-settings.json</c>: each plugin's own keys (→ <c>plugin.&lt;id&gt;.&lt;key&gt;</c>).</item>
/// <item><c>diagnostics\settings.json</c>: whether metadata tracing is on.</item>
/// </list>
/// The old files are left as they were, so a previous build still finds them.
/// </summary>
internal static class LegacySettingsImport
{
    /// <summary>Import what's in <paramref name="directory"/>. True if anything was imported.</summary>
    public static bool Import(SettingsStore store, string directory)
    {
        var any = false;
        any |= ImportFile(Path.Combine(directory, "sources.json"), root => ImportSources(store, root));
        any |= ImportFile(Path.Combine(directory, "plugin-settings.json"), root => ImportPlugins(store, root));
        any |= ImportFile(Path.Combine(directory, "diagnostics", "settings.json"), root =>
            root.TryGetProperty("enabled", out var on) && on.ValueKind == JsonValueKind.True
            && Put(store, MetadataTrace.EnabledSetting, on));
        return any;
    }

    private static bool ImportFile(string path, Func<JsonElement, bool> import)
    {
        if (!File.Exists(path)) return false;
        try
        {
            using var doc = JsonDocument.Parse(File.ReadAllBytes(path));
            if (doc.RootElement.ValueKind != JsonValueKind.Object) return false;
            var imported = import(doc.RootElement);
            if (imported) Trace.WriteLine($"[hzn-settings] imported {path}");
            return imported;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            Trace.WriteLine($"[hzn-settings] couldn't import {path}: {ex.Message}");
            return false;
        }
    }

    // sources.json's top-level fields, and the setting each one became.
    private static readonly (string Field, Setting Setting, JsonValueKind[] Kinds)[] SourceFields =
    [
        ("lastSelected", SourceSettings.LastSelected, [JsonValueKind.String]),
        ("game", GameSelection.Chosen, [JsonValueKind.String]),
        ("targetStation", LegacySettingsMigration.TargetStation, [JsonValueKind.String]),
        ("shuffle", PlaybackSettings.Shuffle, [JsonValueKind.True, JsonValueKind.False]),
        ("previewEnabled", PreviewSettings.Enabled, [JsonValueKind.True, JsonValueKind.False]),
        ("previewDeviceId", PreviewSettings.Device, [JsonValueKind.String]),
        ("previewVolume", PreviewSettings.Volume, [JsonValueKind.Number]),
        ("lyricsKaraoke", LyricsSettings.Karaoke, [JsonValueKind.True, JsonValueKind.False]),
        ("lyricsVersion", LyricsSettings.Version, [JsonValueKind.String]),
        ("lyricsRadioOffset", LyricsSettings.RadioOffset, [JsonValueKind.Number]),
        ("lyricsAudioSync", LyricsSettings.AudioSync, [JsonValueKind.True, JsonValueKind.False]),
        ("searchSourcePriority", SourceSettings.SearchPriority, [JsonValueKind.Array]),
    ];

    private static bool ImportSources(SettingsStore store, JsonElement root)
    {
        var any = false;
        foreach (var (field, setting, kinds) in SourceFields)
        {
            if (root.TryGetProperty(field, out var value) && Array.IndexOf(kinds, value.ValueKind) >= 0)
                any |= Put(store, setting, value);
        }

        if (root.TryGetProperty("perSource", out var perSource) && perSource.ValueKind == JsonValueKind.Object)
        {
            foreach (var source in perSource.EnumerateObject())
            {
                if (source.Value.ValueKind != JsonValueKind.Object) continue;
                foreach (var field in source.Value.EnumerateObject())
                {
                    // A secret was written encrypted; nothing else was.
                    var secret = field.Value.ValueKind == JsonValueKind.String && SecretText.IsProtected(field.Value.GetString());
                    store.Import(SourceConfigStore.KeyFor(source.Name, field.Name), field.Value,
                        secret ? SettingScope.Secret : SourceConfigStore.FieldScope);
                    any = true;
                }
            }
        }
        return any;
    }

    private static bool ImportPlugins(SettingsStore store, JsonElement root)
    {
        if (!root.TryGetProperty("plugins", out var plugins) || plugins.ValueKind != JsonValueKind.Object) return false;
        var any = false;
        foreach (var plugin in plugins.EnumerateObject())
        {
            if (plugin.Value.ValueKind != JsonValueKind.Object) continue;
            foreach (var field in plugin.Value.EnumerateObject())
            {
                if (field.Value.ValueKind != JsonValueKind.String) continue;
                store.Import(PluginSettings.KeyFor(plugin.Name, field.Name), field.Value, PluginSettings.Scope);
                any = true;
            }
        }
        return any;
    }

    private static bool Put(SettingsStore store, Setting setting, JsonElement value)
    {
        store.Import(setting.Key, value, setting.Scope);
        return true;
    }
}
