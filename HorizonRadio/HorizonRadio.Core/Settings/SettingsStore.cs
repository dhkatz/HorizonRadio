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
using HorizonRadio.Sdk;
using HorizonRadio.Sdk.IO;

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
/// devices can tell what may leave this machine and which side changed a setting last. A removed
/// synced setting stays as <c>{ "removed": true, … }</c> with the time it was removed. A secret is
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
    private readonly SecretText _secrets;
    private readonly TimeSpan _saveDelay;
    private readonly TimeProvider _time;
    private readonly Lock _lock = new();
    private readonly Lock _saveLock = new();
    private readonly Dictionary<string, Entry> _entries = new(StringComparer.Ordinal);
    private readonly Timer? _saveTimer;
    private bool _dirty;
    private bool _disposed;

    private SettingsStore(string? path, TimeSpan saveDelay, TimeProvider time, ISecretProtector? secrets)
    {
        _path = path;
        _secrets = new SecretText(secrets ?? ISecretProtector.None);
        _saveDelay = saveDelay;
        _time = time;
        if (path != null) _saveTimer = new Timer(_ => Flush(), null, Timeout.Infinite, Timeout.Infinite);
    }

    /// <summary>Raised with the key of a setting that changed (or was removed), after the change.</summary>
    public event Action<string>? Changed;

    /// <summary>
    /// Load the settings file at <paramref name="path"/> (the app's <see cref="FileName"/>). The
    /// settings kept in the older per-feature files beside it are brought over once
    /// (<see cref="LegacySettingsImport"/>), which leaves those files as they were.
    /// An unreadable file is set aside as <c>settings.json.bad</c> rather than overwritten.
    /// </summary>
    /// <param name="path">The settings file.</param>
    /// <param name="secrets">What secret settings are protected with on disk (the platform's); with
    /// none, they're written as they are, and ones protected elsewhere are kept but can't be read.</param>
    public static SettingsStore Open(string path, ISecretProtector? secrets = null) =>
        Open(path, DefaultSaveDelay, TimeProvider.System, secrets);

    internal static SettingsStore Open(string path, TimeSpan saveDelay, TimeProvider time, ISecretProtector? secrets = null)
    {
        var store = new SettingsStore(path, saveDelay, time, secrets);
        var exists = File.Exists(path);
        if (exists) store.Load(path);
        if (LegacySettingsImport.Import(store, Path.GetDirectoryName(path) ?? ".", firstRun: !exists)) store.Flush();
        return store;
    }

    /// <summary>A store that never touches disk (tests, the designer).</summary>
    public static SettingsStore CreateInMemory(TimeProvider? time = null) =>
        new(null, TimeSpan.Zero, time ?? TimeProvider.System, null);

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
            _entries[key] = new Entry(value.Clone(), scope, Now(), Unreadable: null);
            ScheduleSaveLocked();
        }
        AfterChange(key);
    }

    /// <summary>Forget <paramref name="key"/>. False when it wasn't set. A synced setting leaves a
    /// note of when it was removed, so syncing doesn't bring back another device's older value.</summary>
    public bool Remove(string key)
    {
        lock (_lock)
        {
            if (!_entries.TryGetValue(key, out var old) || old.IsRemoved) return false;
            if (old.Scope == SettingScope.Synced) _entries[key] = Entry.Removed(Now());
            else _entries.Remove(key);
            ScheduleSaveLocked();
        }
        AfterChange(key);
        return true;
    }

    /// <summary>Change whether <paramref name="key"/> syncs (<see cref="SettingScope.Device"/> ↔
    /// <see cref="SettingScope.Synced"/>), keeping its value and when it last changed, so the change
    /// of scope isn't a change of value another device would take. Secrets stay secrets.</summary>
    public void Rescope(string key, SettingScope scope)
    {
        if (scope == SettingScope.Secret) throw new ArgumentException("A setting can't become a secret.", nameof(scope));
        lock (_lock)
        {
            if (!_entries.TryGetValue(key, out var entry) || entry.IsRemoved || entry.Scope == scope || entry.Scope == SettingScope.Secret) return;
            _entries[key] = entry with { Scope = scope };
            ScheduleSaveLocked();
        }
    }

    /// <summary>The keys that start with <paramref name="prefix"/>, in order.</summary>
    public IReadOnlyList<string> KeysWithPrefix(string prefix)
    {
        lock (_lock)
        {
            return [.. _entries.Where(e => !e.Value.IsRemoved && e.Key.StartsWith(prefix, StringComparison.Ordinal)).Select(e => e.Key).Order(StringComparer.Ordinal)];
        }
    }

    /// <summary>The scope <paramref name="key"/> was stored with, or null when it isn't stored.</summary>
    public SettingScope? ScopeOf(string key)
    {
        lock (_lock) return _entries.TryGetValue(key, out var e) && !e.IsRemoved ? e.Scope : null;
    }

    /// <summary>When <paramref name="key"/> last changed, or null when it isn't stored.</summary>
    public DateTimeOffset? ModifiedAt(string key)
    {
        lock (_lock) return _entries.TryGetValue(key, out var e) && !e.IsRemoved ? e.Modified : null;
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
            _entries[key] = Decode(key, stored, scope, Now());
            _dirty = true;
        }
    }

    private void ScheduleSaveLocked()
    {
        _dirty = true;
        if (!_disposed) _saveTimer?.Change(_saveDelay, Timeout.InfiniteTimeSpan);
    }

    /// <summary>Every synced setting as it stands here, removed ones included (with no value), for
    /// another of the user's devices to merge (<see cref="MergeSynced"/>).</summary>
    public IReadOnlyList<SyncedSetting> SyncedSettings()
    {
        lock (_lock)
        {
            return [.. _entries
                .Where(e => e.Value.Scope == SettingScope.Synced)
                .OrderBy(e => e.Key, StringComparer.Ordinal)
                .Select(e => new SyncedSetting(e.Key, e.Value.Value, e.Value.Modified))];
        }
    }

    /// <summary>
    /// Bring in synced settings from another of the user's devices. Each one changed there later than
    /// here replaces this device's (a removal too); on a tie the larger value wins, so two devices
    /// merging each other's settings end up the same. A setting this device keeps to itself
    /// (<see cref="SettingScope.Device"/>, <see cref="SettingScope.Secret"/>) is never replaced.
    /// Raises <see cref="Changed"/> for each setting whose value changed, and returns those keys.
    /// </summary>
    public IReadOnlyList<string> MergeSynced(IEnumerable<SyncedSetting> remote)
    {
        ArgumentNullException.ThrowIfNull(remote);
        var changed = new List<string>();
        lock (_lock)
        {
            var any = false;
            foreach (var setting in remote)
            {
                if (string.IsNullOrWhiteSpace(setting.Key)) continue;
                var known = _entries.TryGetValue(setting.Key, out var local);
                if (known && (local!.Scope != SettingScope.Synced || !Newer(setting, local))) continue;
                _entries[setting.Key] = setting.Value is { } value
                    ? new Entry(value.Clone(), SettingScope.Synced, setting.Modified, Unreadable: null)
                    : Entry.Removed(setting.Modified);
                any = true;
                // A removal of something never set here changes nothing anyone can read.
                if (setting.Value != null || (known && !local!.IsRemoved)) changed.Add(setting.Key);
            }
            if (any) ScheduleSaveLocked();
        }
        foreach (var key in changed) AfterChange(key);
        return changed;
    }

    private static bool Newer(SyncedSetting remote, Entry local)
    {
        if (remote.Modified != local.Modified) return remote.Modified > local.Modified;
        return string.CompareOrdinal(remote.Value?.GetRawText() ?? "", local.Value?.GetRawText() ?? "") > 0;
    }

    // When a change happens, to the millisecond: what the file keeps, so a time read back from it
    // (or from another device) compares equal to the one set.
    private DateTimeOffset Now()
    {
        var now = _time.GetUtcNow();
        return now.AddTicks(-(now.Ticks % TimeSpan.TicksPerMillisecond));
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
                if (prop.Value.ValueKind != JsonValueKind.Object) continue;
                var scope = prop.Value.TryGetProperty("scope", out var s) ? ParseScope(s.GetString()) : SettingScope.Device;
                var modified = prop.Value.TryGetProperty("modified", out var m)
                    && DateTimeOffset.TryParse(m.GetString(), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var at)
                    ? at
                    : DateTimeOffset.UnixEpoch;
                if (scope == SettingScope.Synced && prop.Value.TryGetProperty("removed", out var removed) && removed.ValueKind == JsonValueKind.True)
                    _entries[prop.Name] = Entry.Removed(modified);
                else if (prop.Value.TryGetProperty("value", out var value))
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

    private Entry Decode(string key, JsonElement stored, SettingScope scope, DateTimeOffset modified)
    {
        if (scope == SettingScope.Secret && stored.ValueKind == JsonValueKind.String && stored.GetString() is { } text && SecretText.IsProtected(text))
        {
            var plain = _secrets.Unprotect(text);
            if (plain == null)
            {
                Log($"secret '{key}' can't be decrypted by this user on this machine: kept as stored");
                return new Entry(null, scope, modified, Unreadable: stored.GetString());
            }
            return new Entry(JsonSerializer.SerializeToElement(plain, ValueJson), scope, modified, Unreadable: null);
        }
        return new Entry(stored.Clone(), scope, modified, Unreadable: null);
    }

    private void Write(string path, List<KeyValuePair<string, Entry>> entries)
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
                    JsonElement stored = default;
                    if (!entry.IsRemoved && !TryStoredForm(key, entry, out stored)) continue;
                    writer.WriteStartObject(key);
                    if (entry.IsRemoved)
                    {
                        writer.WriteBoolean("removed", true);
                    }
                    else
                    {
                        writer.WritePropertyName("value");
                        stored.WriteTo(writer);
                    }
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
    private bool TryStoredForm(string key, Entry entry, out JsonElement stored)
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
            stored = JsonSerializer.SerializeToElement(_secrets.Protect(stored.GetString()!), ValueJson);
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

    // Value is null for a secret this user can't decrypt (Unreadable holds it as stored), and for a
    // synced setting that was removed (kept so a sync knows when).
    private sealed record Entry(JsonElement? Value, SettingScope Scope, DateTimeOffset Modified, string? Unreadable)
    {
        public bool IsRemoved => Value == null && Unreadable == null;

        public static Entry Removed(DateTimeOffset at) => new(null, SettingScope.Synced, at, Unreadable: null);
    }

    private sealed class Subscription(Action dispose) : IDisposable
    {
        private Action? _dispose = dispose;

        public void Dispose() => Interlocked.Exchange(ref _dispose, null)?.Invoke();
    }
}
