using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.Json;
using HorizonRadio.Core.Audio;
using HorizonRadio.Core.Diagnostics;
using HorizonRadio.Core.Events;
using HorizonRadio.Core.Games;
using HorizonRadio.Core.Input;
using HorizonRadio.Core.IO;
using HorizonRadio.Core.Lyrics;
using HorizonRadio.Core.Metadata;
using HorizonRadio.Core.Playback;
using HorizonRadio.Core.Plugins;
using HorizonRadio.Core.Sources;
using HorizonRadio.Core.Sources.Config;
using HorizonRadio.Sdk.Metadata;

namespace HorizonRadio.Core.Settings;

/// <summary>
/// Brings the settings kept in per-feature files before <see cref="SettingsStore"/> into it, once
/// per file:
/// <list type="bullet">
/// <item><c>sources.json</c>: the app-level preferences, and each source's form
/// (<c>perSource</c> → <c>source.&lt;id&gt;.&lt;key&gt;</c>, its encrypted secrets as they are).</item>
/// <item><c>plugin-settings.json</c>: each plugin's own keys (→ <c>plugin.&lt;id&gt;.&lt;key&gt;</c>).</item>
/// <item><c>diagnostics\settings.json</c>: whether metadata tracing is on.</item>
/// <item><c>metadata-config.json</c>: the providers' order and overrides, and each provider's form
/// (→ <c>metadata.provider.&lt;id&gt;.&lt;key&gt;</c>; a credential, stored in the clear there,
/// becomes a secret when its form first loads).</item>
/// <item><c>events-config.json</c>: the event rules (→ <c>event.&lt;kind&gt;</c>).</item>
/// <item><c>controls-config.json</c>: the input bindings (→ <c>controls.bindings</c>).</item>
/// </list>
/// The files handled are recorded (<see cref="Imported"/>), so a file added to this list later is
/// still brought into an existing <c>settings.json</c>. The old files are left as they were.
/// </summary>
internal static class LegacySettingsImport
{
    /// <summary>The files handled so far (whether or not they existed), by name.</summary>
    internal static readonly Setting<string[]> Imported = new("settings.imported", SettingScope.Device, []);

    private static readonly (string File, Func<SettingsStore, JsonElement, bool> Import)[] Files =
    [
        ("sources.json", ImportSources),
        ("plugin-settings.json", ImportPlugins),
        (Path.Combine("diagnostics", "settings.json"), ImportDiagnostics),
        ("metadata-config.json", ImportMetadata),
        ("events-config.json", ImportEvents),
        ("controls-config.json", ImportControls),
    ];

    // A settings.json written before Imported was recorded had brought in the first three.
    private const int FilesBeforeImported = 3;

    /// <summary>Import the files in <paramref name="directory"/> not handled yet.
    /// <paramref name="firstRun"/>: there was no <c>settings.json</c>. True if anything was imported
    /// (the store should be saved now).</summary>
    public static bool Import(SettingsStore store, string directory, bool firstRun)
    {
        string[] done = firstRun || store.IsSet(Imported.Key)
            ? store.Get(Imported)
            : [.. Files.Take(FilesBeforeImported).Select(f => f.File)];
        var handled = done.ToList();
        var imported = false;
        foreach (var (file, import) in Files)
        {
            if (done.Contains(file, StringComparer.OrdinalIgnoreCase)) continue;
            var outcome = ImportFile(Path.Combine(directory, file), root => import(store, root));
            if (outcome == Outcome.Unreadable) continue; // tried again next time
            handled.Add(file);
            imported |= outcome == Outcome.Imported;
        }

        // A first run with nothing to bring over writes nothing; otherwise what was handled is
        // recorded (saved with the next change, or now, after an import).
        if (firstRun && !imported) return false;
        if (handled.Count != done.Length || !store.IsSet(Imported.Key)) Put(store, Imported, [.. handled]);
        return imported;
    }

    private enum Outcome { Imported, Nothing, Unreadable }

    private static Outcome ImportFile(string path, Func<JsonElement, bool> import)
    {
        if (!File.Exists(path)) return Outcome.Nothing;
        try
        {
            using var doc = JsonDocument.Parse(File.ReadAllBytes(path));
            if (doc.RootElement.ValueKind != JsonValueKind.Object || !import(doc.RootElement)) return Outcome.Nothing;
            Trace.WriteLine($"[hzn-settings] imported {path}");
            return Outcome.Imported;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Trace.WriteLine($"[hzn-settings] couldn't read {path}: {ex.Message}");
            return Outcome.Unreadable;
        }
        catch (JsonException ex)
        {
            Trace.WriteLine($"[hzn-settings] couldn't import {path}: {ex.Message}");
            return Outcome.Nothing;
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

    private static bool ImportDiagnostics(SettingsStore store, JsonElement root) =>
        root.TryGetProperty("enabled", out var on) && on.ValueKind == JsonValueKind.True
        && Put(store, MetadataTrace.EnabledSetting, on);

    private static bool ImportMetadata(SettingsStore store, JsonElement root)
    {
        var any = false;
        var order = Strings(root, "order");
        // The single provider picked before there was an order.
        if (order is not { Length: > 0 } && root.TryGetProperty("selected", out var selected)
            && StringOrNull(selected) is { Length: > 0 } legacy && legacy != MetadataCatalog.NoneId)
            order = [legacy];
        if (order != null) any |= Put(store, MetadataSettings.Order, order);
        if (Strings(root, "introduced") is { } introduced) any |= Put(store, MetadataSettings.Introduced, introduced);

        if (root.TryGetProperty("forced", out var forced) && forced.ValueKind == JsonValueKind.Object)
        {
            var byField = new Dictionary<MetadataField, string>();
            foreach (var prop in forced.EnumerateObject())
            {
                if (Enum.TryParse<MetadataField>(prop.Name, out var field) && StringOrNull(prop.Value) is { Length: > 0 } id)
                    byField[field] = id;
            }
            if (byField.Count > 0) any |= Put(store, MetadataSettings.Forced, byField);
        }

        if (root.TryGetProperty("titleModelMode", out var mode)
            && Enum.TryParse<TitleModelMode>(StringOrNull(mode), ignoreCase: true, out var titleModel))
            any |= Put(store, MetadataSettings.TitleModel, titleModel);

        if (root.TryGetProperty("perProvider", out var perProvider) && perProvider.ValueKind == JsonValueKind.Object)
        {
            foreach (var provider in perProvider.EnumerateObject())
            {
                if (provider.Value.ValueKind != JsonValueKind.Object) continue;
                foreach (var field in provider.Value.EnumerateObject())
                {
                    if (field.Value.ValueKind is JsonValueKind.Object or JsonValueKind.Array) continue;
                    store.Import(MetadataConfigStore.KeyFor(provider.Name, field.Name), field.Value, MetadataConfigStore.FieldScope);
                    any = true;
                }
            }
        }
        return any;
    }

    private static bool ImportEvents(SettingsStore store, JsonElement root)
    {
        if (!root.TryGetProperty("rules", out var rules) || rules.ValueKind != JsonValueKind.Object) return false;
        var any = false;
        foreach (var rule in rules.EnumerateObject())
        {
            if (rule.Value.ValueKind != JsonValueKind.Object || !rule.Value.TryGetProperty("type", out var t)) continue;
            // Mixes were called profiles.
            var typeName = StringOrNull(t) switch
            {
                "SwitchProfile" => nameof(EventActionType.SwitchMix),
                "NextProfile" => nameof(EventActionType.NextMix),
                "PreviousProfile" => nameof(EventActionType.PreviousMix),
                var other => other,
            };
            if (!Enum.TryParse<EventActionType>(typeName, out var type) || type == EventActionType.None) continue;
            var param = rule.Value.TryGetProperty("param", out var p) ? StringOrNull(p) : null;
            any |= Put(store, EventRuleStore.RuleFor(rule.Name), new EventAction(type, param));
        }
        return any;
    }

    private static bool ImportControls(SettingsStore store, JsonElement root)
    {
        if (!root.TryGetProperty("bindings", out var items) || items.ValueKind != JsonValueKind.Array) return false;
        var bindings = new List<StoredBinding>();
        foreach (var item in items.EnumerateArray())
        {
            // Stored the same way; a binding that doesn't read is skipped, as it was.
            if (item.ValueKind != JsonValueKind.Object || !item.TryGetProperty("code", out var code) || code.ValueKind != JsonValueKind.Number)
                continue;
            try
            {
                if (item.Deserialize<StoredBinding>(SettingsStore.ValueJson) is { Action: { } } binding) bindings.Add(binding);
            }
            catch (JsonException)
            {
            }
        }
        return bindings.Count > 0 && Put(store, InputBindingStore.Bindings, [.. bindings]);
    }

    private static string[]? Strings(JsonElement root, string name) =>
        root.TryGetProperty(name, out var array) && array.ValueKind == JsonValueKind.Array
            ? [.. array.EnumerateArray().Select(StringOrNull).OfType<string>().Where(s => s.Length > 0)]
            : null;

    private static string? StringOrNull(JsonElement value) =>
        value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    private static bool Put(SettingsStore store, Setting setting, JsonElement value)
    {
        store.Import(setting.Key, value, setting.Scope);
        return true;
    }

    private static bool Put<T>(SettingsStore store, Setting<T> setting, T value) =>
        Put(store, setting, JsonSerializer.SerializeToElement(value, SettingsStore.ValueJson));
}
