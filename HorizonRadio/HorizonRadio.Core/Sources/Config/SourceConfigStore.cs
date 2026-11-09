using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.Json;
using HorizonRadio.Core.IO;

namespace HorizonRadio.Core.Sources.Config;

/// <summary>
/// Persists per-source config + the last-selected source id to
/// <c>%LOCALAPPDATA%\HorizonRadio\sources.json</c>. Loaded once at
/// startup; saved whenever the user changes a value or starts a source.
///
/// File shape (intentionally hand-editable):
/// <code>
/// {
///   "lastSelected": "local",
///   "perSource": {
///     "local":    { "path": "C:/Music" },
///     "testtone": { "frequency": "440" }
///   }
/// }
/// </code>
///
/// A secret field (<see cref="TextField.IsSecret"/>) is written encrypted (<see cref="SecretText"/>)
/// and read back in the clear; the rest is plain.
///
/// Storage uses <see cref="object"/> values to round-trip JSON
/// primitives (string/bool/number); <see cref="ConfigValues"/>'s typed
/// accessors handle the boxing.
/// </summary>
public sealed class SourceConfigStore
{
    public string? LastSelectedId { get; set; }

    /// <summary>LEGACY: the default target station from before games were plugins. Read only so
    /// <c>LegacySettingsMigration.MoveTargetStation</c> can move it into the FH6 plugin's
    /// settings, which then clears it; it's no longer written once cleared.</summary>
    public string? TargetStation { get; set; }

    /// <summary>The game Horizon Radio runs with: a game plugin's id, <c>"none"</c> for no game
    /// (a plain player), or null if the user hasn't chosen yet (first run). Takes effect on
    /// startup.</summary>
    public string? GameId { get; set; }

    /// <summary>Global shuffle preference (applies to whichever source is
    /// active). Persisted so the choice survives restarts.</summary>
    public bool Shuffle { get; set; }

    /// <summary>Whether to also play the active source's audio out of a local
    /// speaker ("test playback"), independent of the in-game pipe.</summary>
    public bool PreviewEnabled { get; set; }

    /// <summary>Render endpoint id for preview playback (null = system
    /// default). Matches <c>MMDevice.ID</c>.</summary>
    public string? PreviewDeviceId { get; set; }

    /// <summary>Master volume slider <em>position</em> in [0, 1] (persisted as
    /// <c>previewVolume</c> for back-compat). Tapered to a perceptual gain and
    /// applied to both the local monitor and the in-game bridge pre-amp. The
    /// default (0.75 → cubic gain ≈ 0.42 ≈ -7.5 dBFS) is calibrated by ear to sit
    /// on par with FH6's own stations; the game's music fader scales our injected
    /// audio and the real stations together, so the match holds across in-game
    /// volume settings. Tune from here.</summary>
    public double PreviewVolume { get; set; } = 0.75;

    /// <summary>Source ids in the user's preferred order for unified search — the default
    /// "Play" on a merged result uses the highest-priority source it has (the per-result
    /// picker overrides per row). Empty = fall back to catalog order. Persisted so the
    /// choice survives restarts.</summary>
    public List<string> SearchSourcePriority { get; set; } = new();

    /// <summary>Whether synced lyrics fill in karaoke-style as they're sung (word by word where
    /// they're word-timed). Off highlights the whole line at once.</summary>
    public bool LyricsKaraoke { get; set; } = true;

    /// <summary>The lyrics version last picked ("Romaji", "English"), shown by default whenever a
    /// song has it; null = the original.</summary>
    public string? LyricsVersion { get; set; }

    /// <summary>How much earlier to show radio lyrics than the station's title change suggests,
    /// in seconds (negative: later).</summary>
    public double LyricsRadioOffset { get; set; }

    /// <summary>Whether a radio song's place is found by lining the station's audio up against a
    /// recording of the song (downloaded once per song), so its synced lyrics follow along.</summary>
    public bool LyricsAudioSync { get; set; } = true;

    private readonly Dictionary<string, Dictionary<string, object?>> _perSource = new();

    // Serializes SaveToDisk: two saves (e.g. a UI change and a debounced flush) must not
    // write the file at the same time.
    private readonly object _saveLock = new();

    private static string DefaultPath =>
        Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "HorizonRadio",
            "sources.json");

    private static void Log(string msg) => Trace.WriteLine($"[hzn-cfg] {msg}");

    /// <summary>Read all values previously stored for <paramref name="sourceId"/>.
    /// Returns a fresh <see cref="ConfigValues"/> populated with schema defaults
    /// if no entry exists yet, so callers always get a usable bag.</summary>
    public ConfigValues Load(string sourceId, IReadOnlyList<ConfigField> schema)
    {
        foreach (var secret in schema.OfType<TextField>().Where(f => f.IsSecret))
            MarkSecret(sourceId, secret.Key);
        var values = new ConfigValues();
        if (_perSource.TryGetValue(sourceId, out var stored))
        {
            foreach (var (k, v) in stored) values.Set(k, v);
        }
        values.ApplyDefaults(schema);
        return values;
    }

    // Keys written encrypted, per source: the schema's secret fields (seen on Load) and any value
    // that was encrypted on disk, so a secret stays encrypted even if saved before its schema loads.
    private readonly Dictionary<string, HashSet<string>> _secretKeys = new();

    // Secrets on disk this Windows user can't decrypt (sources.json copied from another account or
    // PC, or the account's keys reset), kept as stored so a save doesn't erase them; a new value
    // replaces one.
    private readonly Dictionary<(string Source, string Key), string> _unreadable = new();

    private void MarkSecret(string sourceId, string key)
    {
        lock (_secretKeys)
        {
            if (!_secretKeys.TryGetValue(sourceId, out var keys)) _secretKeys[sourceId] = keys = new HashSet<string>();
            keys.Add(key);
        }
    }

    private bool IsSecret(string sourceId, string key)
    {
        lock (_secretKeys) return _secretKeys.TryGetValue(sourceId, out var keys) && keys.Contains(key);
    }

    /// <summary>Replace the stored values for one source.</summary>
    public void Save(string sourceId, ConfigValues values)
    {
        _perSource[sourceId] = new Dictionary<string, object?>(values.AsReadOnly());
    }

    public static SourceConfigStore LoadFromDisk(string? path = null)
    {
        path ??= DefaultPath;
        var store = new SourceConfigStore();
        try
        {
            if (!File.Exists(path)) return store;

            using var stream = File.OpenRead(path);
            using var doc = JsonDocument.Parse(stream);
            var root = doc.RootElement;

            if (root.TryGetProperty("lastSelected", out var sel) && sel.ValueKind == JsonValueKind.String)
                store.LastSelectedId = sel.GetString();

            if (root.TryGetProperty("game", out var game) && game.ValueKind == JsonValueKind.String)
                store.GameId = game.GetString();
            if (root.TryGetProperty("targetStation", out var tgt) && tgt.ValueKind == JsonValueKind.String)
                store.TargetStation = tgt.GetString();

            if (root.TryGetProperty("shuffle", out var shuf) &&
                (shuf.ValueKind == JsonValueKind.True || shuf.ValueKind == JsonValueKind.False))
                store.Shuffle = shuf.GetBoolean();

            if (root.TryGetProperty("previewEnabled", out var pe) &&
                (pe.ValueKind == JsonValueKind.True || pe.ValueKind == JsonValueKind.False))
                store.PreviewEnabled = pe.GetBoolean();

            if (root.TryGetProperty("previewDeviceId", out var pd) && pd.ValueKind == JsonValueKind.String)
                store.PreviewDeviceId = pd.GetString();

            if (root.TryGetProperty("previewVolume", out var pv) && pv.ValueKind == JsonValueKind.Number)
                store.PreviewVolume = pv.GetDouble();

            if (root.TryGetProperty("lyricsKaraoke", out var lk) &&
                (lk.ValueKind == JsonValueKind.True || lk.ValueKind == JsonValueKind.False))
                store.LyricsKaraoke = lk.GetBoolean();

            if (root.TryGetProperty("lyricsVersion", out var lv) && lv.ValueKind == JsonValueKind.String)
                store.LyricsVersion = lv.GetString();

            if (root.TryGetProperty("lyricsRadioOffset", out var lo) && lo.ValueKind == JsonValueKind.Number)
                store.LyricsRadioOffset = lo.GetDouble();

            if (root.TryGetProperty("lyricsAudioSync", out var la) &&
                (la.ValueKind == JsonValueKind.True || la.ValueKind == JsonValueKind.False))
                store.LyricsAudioSync = la.GetBoolean();

            if (root.TryGetProperty("searchSourcePriority", out var sp) && sp.ValueKind == JsonValueKind.Array)
            {
                foreach (var id in sp.EnumerateArray())
                    if (id.ValueKind == JsonValueKind.String && id.GetString() is { } s)
                        store.SearchSourcePriority.Add(s);
            }

            if (root.TryGetProperty("perSource", out var per) && per.ValueKind == JsonValueKind.Object)
            {
                foreach (var src in per.EnumerateObject())
                {
                    if (src.Value.ValueKind != JsonValueKind.Object) continue;
                    var bag = new Dictionary<string, object?>();
                    foreach (var prop in src.Value.EnumerateObject())
                    {
                        var value = JsonElementToObject(prop.Value);
                        if (value is string text && SecretText.IsProtected(text))
                        {
                            store.MarkSecret(src.Name, prop.Name);
                            value = SecretText.Unprotect(text);
                            if (value == null) store._unreadable[(src.Name, prop.Name)] = text;
                        }
                        bag[prop.Name] = value;
                    }
                    store._perSource[src.Name] = bag;
                }
            }
        }
        catch (Exception ex)
        {
            Log($"load failed (using empty store): {ex.Message}");
        }
        return store;
    }

    public void SaveToDisk(string? path = null)
    {
        path ??= DefaultPath;
        lock (_saveLock)
        {
            SaveLocked(path);
        }
    }

    // Written to a temp file and renamed over sources.json, so a save that throws partway
    // (or a crash) leaves the previous settings intact instead of a truncated file.
    private void SaveLocked(string path)
    {
        try
        {
            AtomicFile.Write(path, stream =>
            {
                using var writer = new Utf8JsonWriter(stream, new JsonWriterOptions { Indented = true });

                writer.WriteStartObject();
                if (LastSelectedId != null) writer.WriteString("lastSelected", LastSelectedId);
                if (GameId != null) writer.WriteString("game", GameId);
                if (TargetStation != null) writer.WriteString("targetStation", TargetStation);
                writer.WriteBoolean("shuffle", Shuffle);
                writer.WriteBoolean("previewEnabled", PreviewEnabled);
                if (PreviewDeviceId != null) writer.WriteString("previewDeviceId", PreviewDeviceId);
                writer.WriteNumber("previewVolume", PreviewVolume);
                writer.WriteBoolean("lyricsKaraoke", LyricsKaraoke);
                if (LyricsVersion != null) writer.WriteString("lyricsVersion", LyricsVersion);
                if (LyricsRadioOffset != 0) writer.WriteNumber("lyricsRadioOffset", LyricsRadioOffset);
                writer.WriteBoolean("lyricsAudioSync", LyricsAudioSync);
                if (SearchSourcePriority.Count > 0)
                {
                    writer.WriteStartArray("searchSourcePriority");
                    foreach (var id in SearchSourcePriority) writer.WriteStringValue(id);
                    writer.WriteEndArray();
                }
                writer.WriteStartObject("perSource");
                foreach (var (sourceId, bag) in _perSource)
                {
                    writer.WriteStartObject(sourceId);
                    foreach (var (k, v) in bag)
                    {
                        if (!TryStoredForm(sourceId, k, v, out var stored)) continue;
                        WriteValue(writer, k, stored);
                    }
                    writer.WriteEndObject();
                }
                writer.WriteEndObject();
                writer.WriteEndObject();
            });
        }
        catch (Exception ex)
        {
            Log($"save failed: {ex.Message}");
        }
    }

    // What a value is written as: a secret encrypted (or as it was, when it couldn't be read and
    // nothing replaced it). False to leave it out: a secret that couldn't be encrypted is never
    // written in plain text, and the rest of the settings still save.
    private bool TryStoredForm(string sourceId, string key, object? value, out object? stored)
    {
        stored = value;
        if (!IsSecret(sourceId, key)) return true;
        if (value is not string { Length: > 0 } plain)
        {
            if (_unreadable.TryGetValue((sourceId, key), out var kept)) stored = kept;
            return true;
        }
        _unreadable.Remove((sourceId, key));
        try
        {
            stored = SecretText.Protect(plain);
            return true;
        }
        catch (System.Security.Cryptography.CryptographicException ex)
        {
            Log($"a secret setting couldn't be encrypted, so it isn't saved: {ex.Message}");
            return false;
        }
    }

    private static object? JsonElementToObject(JsonElement el) => el.ValueKind switch
    {
        JsonValueKind.String => el.GetString(),
        JsonValueKind.True => true,
        JsonValueKind.False => false,
        JsonValueKind.Number => el.TryGetInt64(out var i) ? i : el.GetDouble(),
        JsonValueKind.Null => null,
        _ => el.GetRawText(),
    };

    private static void WriteValue(Utf8JsonWriter w, string name, object? v)
    {
        switch (v)
        {
            case null: w.WriteNull(name); break;
            case string s: w.WriteString(name, s); break;
            case bool b: w.WriteBoolean(name, b); break;
            case int i: w.WriteNumber(name, i); break;
            case long l: w.WriteNumber(name, l); break;
            case double d: w.WriteNumber(name, d); break;
            default: w.WriteString(name, v.ToString() ?? ""); break;
        }
    }
}
