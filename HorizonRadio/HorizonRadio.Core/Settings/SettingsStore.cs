using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text.Json;
using System.Threading;
using HorizonRadio.Core.IO;

namespace HorizonRadio.Core.Settings;

/// <summary>
/// Every setting the app keeps, in one hand-editable file: <c>settings.json</c> in
/// <see cref="AppPaths.Root"/>. A setting is declared where it's used (<see cref="Setting{T}"/>) and
/// read and written here; settings whose key is only known at run time (a source's form fields, a
/// plugin's own keys) use the raw members (<see cref="TryGetRaw"/>, <see cref="SetRaw"/>).
/// <code>
/// {
///   "version": 1,
///   "settings": {
///     "lyrics.karaoke": { "value": true, "scope": "synced", "modified": "2026-11-10T09:12:33.123Z" },
///     "source.spotify-driven.soloistKey": { "value": "dpapi:AQAAANCMnd8B…", "scope": "secret", "modified": "…" }
///   }
/// }
/// </code>
/// Each value carries its <see cref="SettingScope"/> and when it was last changed, so syncing your
/// devices can tell what may leave this machine and which side changed a setting last. A secret is
/// encrypted on disk and read back in the clear.
///
/// Thread-safe. A change raises <see cref="Changed"/> (on the thread that made it) and is saved a
/// moment later, so a slider dragged across fifty values writes the file once; <see cref="Flush"/>
/// (and <see cref="Dispose"/>) write it at once; after Dispose, every change is written at once.
/// </summary>
public sealed class SettingsStore : IDisposable
{
    public const string FileName = "settings.json";
    private const int FormatVersion = 1;

    private static readonly TimeSpan DefaultSaveDelay = TimeSpan.FromMilliseconds(300);

    // NaN and infinity are written as "NaN" / "Infinity" rather than refused: a setting that ends
    // up one shouldn't fail the save of every other. Enums are stored by name, which survives a
    // reordering and reads well in the file.
    internal static readonly JsonSerializerOptions ValueJson = new(JsonSerializerDefaults.Web)
    {
        NumberHandling = System.Text.Json.Serialization.JsonNumberHandling.AllowNamedFloatingPointLiterals,
        Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter() },
    };

    private readonly string? _path; // null = in memory only
    private readonly TimeSpan _saveDelay;
    private readonly TimeProvider _time;
    private readonly Lock _lock = new();
    private readonly Lock _saveLock = new();
    private readonly Dictionary<string, Entry> _entries = new(StringComparer.Ordinal);
    private readonly Timer? _saveTimer;
    private bool _dirty;
    private bool _disposed;

    private SettingsStore(string? path, TimeSpan saveDelay, TimeProvider time)
    {
        _path = path;
        _saveDelay = saveDelay;
        _time = time;
        if (path != null) _saveTimer = new Timer(_ => Flush(), null, Timeout.Infinite, Timeout.Infinite);
    }

    /// <summary>Raised with the key of a setting that changed (or was removed), after the change.</summary>
    public event Action<string>? Changed;

    public static string DefaultPath => AppPaths.In(FileName);

    /// <summary>
    /// Load the settings file at <paramref name="path"/> (default <see cref="DefaultPath"/>). The
    /// settings kept in the older per-feature files beside it are brought over once
    /// (<see cref="LegacySettingsImport"/>), which leaves those files as they were.
    /// An unreadable file is set aside as <c>settings.json.bad</c> rather than overwritten.
    /// </summary>
    public static SettingsStore Open(string? path = null) => Open(path ?? DefaultPath, DefaultSaveDelay, TimeProvider.System);

    internal static SettingsStore Open(string path, TimeSpan saveDelay, TimeProvider time)
    {
        var store = new SettingsStore(path, saveDelay, time);
        var exists = File.Exists(path);
        if (exists) store.Load(path);
        if (LegacySettingsImport.Import(store, Path.GetDirectoryName(path) ?? ".", firstRun: !exists)) store.Flush();
        return store;
    }

    /// <summary>A store that never touches disk (tests, the designer).</summary>
    public static SettingsStore CreateInMemory(TimeProvider? time = null) =>
        new(null, TimeSpan.Zero, time ?? TimeProvider.System);

    /// <summary>The setting's value, or its <see cref="Setting{T}.Default"/> when it isn't set (or
    /// what's stored isn't a <typeparamref name="T"/>, e.g. after a hand edit).</summary>
    public T Get<T>(Setting<T> setting)
    {
        ArgumentNullException.ThrowIfNull(setting);
        if (!TryGetRaw(setting.Key, out var raw)) return setting.Default;
        try
        {
            return raw.Deserialize<T>(ValueJson)!;
        }
        catch (Exception ex) when (ex is JsonException or NotSupportedException or InvalidOperationException)
        {
            Log($"'{setting.Key}' isn't a {typeof(T).Name} ({raw.GetRawText()}): using its default");
            return setting.Default;
        }
    }

    /// <summary>Store <paramref name="value"/> (a value equal to what's stored changes nothing).</summary>
    public void Set<T>(Setting<T> setting, T value)
    {
        ArgumentNullException.ThrowIfNull(setting);
        SetRaw(setting.Key, JsonSerializer.SerializeToElement(value, ValueJson), setting.Scope);
    }

    /// <summary>Forget the setting, so it reads as its default again.</summary>
    public void Reset(Setting setting)
    {
        ArgumentNullException.ThrowIfNull(setting);
        Remove(setting.Key);
    }

    /// <summary>Whether <paramref name="key"/> has a value of its own (not its default).</summary>
    public bool IsSet(string key)
    {
        lock (_lock) return _entries.TryGetValue(key, out var e) && e.Value != null;
    }

    /// <summary>The stored JSON value of <paramref name="key"/>. False when it isn't set, or it's a
    /// secret this Windows user can't decrypt.</summary>
    public bool TryGetRaw(string key, out JsonElement value)
    {
        lock (_lock)
        {
            if (_entries.TryGetValue(key, out var e) && e.Value is { } v)
            {
                value = v;
                return true;
            }
        }
        value = default;
        return false;
    }

    /// <summary>
    /// Store a JSON value under <paramref name="key"/> with <paramref name="scope"/>. A secret holds
    /// text (or null). A secret on disk this user can't decrypt (the file came from another account
    /// or PC) is kept as it is until a non-empty value replaces it.
    /// </summary>
    public void SetRaw(string key, JsonElement value, SettingScope scope)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        if (scope == SettingScope.Secret && value.ValueKind is not (JsonValueKind.String or JsonValueKind.Null))
            throw new ArgumentException($"A secret setting holds text; '{key}' was given {value.ValueKind}.", nameof(value));

        lock (_lock)
        {
            if (_entries.TryGetValue(key, out var old))
            {
                if (old.Scope == scope && old.Value is { } current && JsonElement.DeepEquals(current, value)) return;
                if (old.Unreadable != null && scope == SettingScope.Secret && IsBlank(value)) return;
            }
            _entries[key] = new Entry(value.Clone(), scope, _time.GetUtcNow(), Unreadable: null);
            ScheduleSaveLocked();
        }
        AfterChange(key);
    }

    /// <summary>Forget <paramref name="key"/>. False when it wasn't set.</summary>
    public bool Remove(string key)
    {
        lock (_lock)
        {
            if (!_entries.Remove(key)) return false;
            ScheduleSaveLocked();
        }
        AfterChange(key);
        return true;
    }

    /// <summary>The keys that start with <paramref name="prefix"/>, in order.</summary>
    public IReadOnlyList<string> KeysWithPrefix(string prefix)
    {
        lock (_lock)
        {
            return [.. _entries.Keys.Where(k => k.StartsWith(prefix, StringComparison.Ordinal)).Order(StringComparer.Ordinal)];
        }
    }

    /// <summary>The scope <paramref name="key"/> was stored with, or null when it isn't stored.</summary>
    public SettingScope? ScopeOf(string key)
    {
        lock (_lock) return _entries.TryGetValue(key, out var e) ? e.Scope : null;
    }

    /// <summary>When <paramref name="key"/> last changed, or null when it isn't stored.</summary>
    public DateTimeOffset? ModifiedAt(string key)
    {
        lock (_lock) return _entries.TryGetValue(key, out var e) ? e.Modified : null;
    }

    /// <summary>Call <paramref name="onChange"/> with the setting's new value whenever it changes.
    /// Dispose the result to stop.</summary>
    public IDisposable Watch<T>(Setting<T> setting, Action<T> onChange)
    {
        ArgumentNullException.ThrowIfNull(setting);
        ArgumentNullException.ThrowIfNull(onChange);
        void Handler(string key)
        {
            if (key == setting.Key) onChange(Get(setting));
        }
        Changed += Handler;
        return new Subscription(() => Changed -= Handler);
    }

    /// <summary>Write pending changes now.</summary>
    public void Flush()
    {
        if (_path == null) return;
        lock (_saveLock)
        {
            List<KeyValuePair<string, Entry>> snapshot;
            lock (_lock)
            {
                if (!_dirty) return;
                _dirty = false;
                snapshot = [.. _entries.OrderBy(e => e.Key, StringComparer.Ordinal)];
            }
            Write(_path, snapshot);
        }
    }

    public void Dispose()
    {
        lock (_lock)
        {
            if (_disposed) return;
            _disposed = true;
        }
        _saveTimer?.Dispose();
        Flush();
    }

    // Called by LegacySettingsImport: a value in its on-disk form (a secret still encrypted), stored
    // without raising Changed.
    internal void Import(string key, JsonElement stored, SettingScope scope)
    {
        lock (_lock)
        {
            _entries[key] = Decode(key, stored, scope, _time.GetUtcNow());
            _dirty = true;
        }
    }

    private void ScheduleSaveLocked()
    {
        _dirty = true;
        if (!_disposed) _saveTimer?.Change(_saveDelay, Timeout.InfiniteTimeSpan);
    }

    // A change after Dispose (a slider moved during shutdown) is written at once: there's no timer.
    private void AfterChange(string key)
    {
        if (_disposed) Flush();
        Changed?.Invoke(key);
    }

    private static bool IsBlank(JsonElement value) =>
        value.ValueKind == JsonValueKind.Null || (value.ValueKind == JsonValueKind.String && value.GetString()!.Length == 0);

    private void Load(string path)
    {
        try
        {
            using var doc = JsonDocument.Parse(File.ReadAllBytes(path));
            if (!doc.RootElement.TryGetProperty("settings", out var all) || all.ValueKind != JsonValueKind.Object) return;
            foreach (var prop in all.EnumerateObject())
            {
                if (prop.Value.ValueKind != JsonValueKind.Object || !prop.Value.TryGetProperty("value", out var value)) continue;
                var scope = prop.Value.TryGetProperty("scope", out var s) ? ParseScope(s.GetString()) : SettingScope.Device;
                var modified = prop.Value.TryGetProperty("modified", out var m)
                    && DateTimeOffset.TryParse(m.GetString(), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var at)
                    ? at
                    : DateTimeOffset.UnixEpoch;
                _entries[prop.Name] = Decode(prop.Name, value, scope, modified);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or InvalidOperationException)
        {
            Log($"couldn't read {path} ({ex.Message}): starting from defaults; the file is kept as {FileName}.bad");
            try { File.Copy(path, path + ".bad", overwrite: true); }
            catch (Exception copyEx) when (copyEx is IOException or UnauthorizedAccessException) { }
            _entries.Clear();
        }
    }

    private static Entry Decode(string key, JsonElement stored, SettingScope scope, DateTimeOffset modified)
    {
        if (scope == SettingScope.Secret && stored.ValueKind == JsonValueKind.String && stored.GetString() is { } text && SecretText.IsProtected(text))
        {
            var plain = SecretText.Unprotect(text);
            if (plain == null)
            {
                Log($"secret '{key}' can't be decrypted by this Windows user: kept as stored");
                return new Entry(null, scope, modified, Unreadable: stored.GetString());
            }
            return new Entry(JsonSerializer.SerializeToElement(plain, ValueJson), scope, modified, Unreadable: null);
        }
        return new Entry(stored.Clone(), scope, modified, Unreadable: null);
    }

    private static void Write(string path, List<KeyValuePair<string, Entry>> entries)
    {
        try
        {
            AtomicFile.Write(path, stream =>
            {
                using var writer = new Utf8JsonWriter(stream, new JsonWriterOptions { Indented = true });
                writer.WriteStartObject();
                writer.WriteNumber("version", FormatVersion);
                writer.WriteStartObject("settings");
                foreach (var (key, entry) in entries)
                {
                    if (!TryStoredForm(key, entry, out var stored)) continue;
                    writer.WriteStartObject(key);
                    writer.WritePropertyName("value");
                    stored.WriteTo(writer);
                    writer.WriteString("scope", FormatScope(entry.Scope));
                    writer.WriteString("modified", entry.Modified.UtcDateTime.ToString("yyyy-MM-ddTHH:mm:ss.fffZ", CultureInfo.InvariantCulture));
                    writer.WriteEndObject();
                }
                writer.WriteEndObject();
                writer.WriteEndObject();
            });
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Log($"save failed: {ex.Message}");
        }
    }

    // What a value is written as: a secret encrypted, or as it was when it couldn't be decrypted.
    // False leaves it out: a secret that can't be encrypted is never written in plain text.
    private static bool TryStoredForm(string key, Entry entry, out JsonElement stored)
    {
        if (entry.Unreadable != null)
        {
            stored = JsonSerializer.SerializeToElement(entry.Unreadable, ValueJson);
            return true;
        }
        stored = entry.Value!.Value;
        if (entry.Scope != SettingScope.Secret || IsBlank(stored)) return true;
        try
        {
            stored = JsonSerializer.SerializeToElement(SecretText.Protect(stored.GetString()!), ValueJson);
            return true;
        }
        catch (CryptographicException ex)
        {
            Log($"secret '{key}' couldn't be encrypted, so it isn't saved: {ex.Message}");
            return false;
        }
    }

    private static SettingScope ParseScope(string? text) => text switch
    {
        "synced" => SettingScope.Synced,
        "secret" => SettingScope.Secret,
        _ => SettingScope.Device, // unknown: keep it on this machine
    };

    private static string FormatScope(SettingScope scope) => scope switch
    {
        SettingScope.Synced => "synced",
        SettingScope.Secret => "secret",
        _ => "device",
    };

    private static void Log(string message) => Trace.WriteLine($"[hzn-settings] {message}");

    // Value is null only for a secret this user can't decrypt (Unreadable holds it as stored).
    private sealed record Entry(JsonElement? Value, SettingScope Scope, DateTimeOffset Modified, string? Unreadable);

    private sealed class Subscription(Action dispose) : IDisposable
    {
        private Action? _dispose = dispose;

        public void Dispose() => Interlocked.Exchange(ref _dispose, null)?.Invoke();
    }
}
