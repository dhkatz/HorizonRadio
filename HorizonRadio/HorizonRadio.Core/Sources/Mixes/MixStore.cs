using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.Json;
using HorizonRadio.Sdk.IO;
using HorizonRadio.Sdk.Sources;

namespace HorizonRadio.Core.Sources.Mixes;

/// <summary>
/// Persists the user's mixes to <c>mixes.json</c> in the app's folder,
/// in the same hand-editable JSON style as the other stores. CRUD mutations
/// raise <see cref="Changed"/> so the Mixes tab and the player-bar switcher stay
/// in sync off one instance; saving is the caller's call via
/// <see cref="SaveToDisk"/>.
///
/// File shape:
/// <code>
/// {
///   "mixes": [
///     {
///       "id": "…", "name": "Drive", "modified": "2026-11-10T21:02:15.123Z",
///       "entries": [
///         { "source": "youtube", "locator": "https://…", "name": "Synthwave mix" },
///         { "source": "local",   "locator": "C:\\Music\\foo.flac" }
///       ]
///     }
///   ],
///   "removed": [ { "id": "…", "modified": "…" } ]
/// }
/// </code>
/// Each mix records when it last changed, and a deleted one when it went, so your devices can keep
/// their mixes in step (<see cref="SyncedMixes"/>, <see cref="MergeSynced"/>). Safe from any thread:
/// the user edits from the window, and a merge arrives from another device on the helper's.
/// </summary>
public sealed class MixStore
{
    private readonly Lock _lock = new();
    private readonly List<Mix> _mixes = new();
    private readonly Dictionary<string, DateTimeOffset> _modified = new(StringComparer.Ordinal);
    private readonly Dictionary<string, DateTimeOffset> _removed = new(StringComparer.Ordinal);

    /// <summary>The clock changes are timed by (tests set their own).</summary>
    public TimeProvider Clock { get; init; } = TimeProvider.System;

    /// <summary>Raised after any add/update/remove or merge, on the thread that made it (a UI
    /// marshals to its own).</summary>
    public event Action? Changed;

    /// <summary>The mixes as they are now (a copy).</summary>
    public IReadOnlyList<Mix> All
    {
        get
        {
            lock (_lock) return [.. _mixes];
        }
    }

    private readonly Dictionary<string, string> _legacyStations = new(StringComparer.Ordinal);

    /// <summary>
    /// LEGACY: mix id → the in-game station a mix used to override, read from pre-plugin files
    /// (<c>"station"</c> on a mix). No longer written; <c>LegacySettingsMigration</c> moves these
    /// into the FH6 plugin's station assignments.
    /// </summary>
    public IReadOnlyDictionary<string, string> LegacyStations => _legacyStations;

    public Mix? Get(string id)
    {
        lock (_lock) return Find(id);
    }

    private Mix? Find(string id) => _mixes.FirstOrDefault(m => m.Id == id);

    /// <summary>Insert a new mix or replace the one with the same id, then notify.</summary>
    public void AddOrUpdate(Mix mix)
    {
        ArgumentNullException.ThrowIfNull(mix);
        lock (_lock)
        {
            var i = _mixes.FindIndex(m => m.Id == mix.Id);
            if (i >= 0) _mixes[i] = mix;
            else _mixes.Add(mix);
            _modified[mix.Id] = Now();
            _removed.Remove(mix.Id);
        }
        Changed?.Invoke();
    }

    public void Remove(string id)
    {
        lock (_lock)
        {
            if (_mixes.RemoveAll(m => m.Id == id) == 0) return;
            _modified.Remove(id);
            _removed[id] = Now();
        }
        Changed?.Invoke();
    }

    /// <summary>When a mix last changed (a mix from before this was kept: long ago).</summary>
    public DateTimeOffset ModifiedAt(string id)
    {
        lock (_lock) return ModifiedAtLocked(id);
    }

    private DateTimeOffset ModifiedAtLocked(string id) =>
        _modified.TryGetValue(id, out var at) ? at : _removed.TryGetValue(id, out var gone) ? gone : DateTimeOffset.UnixEpoch;

    /// <summary>Every mix, and every deleted one, with when it last changed: for another of your
    /// devices to merge.</summary>
    public IReadOnlyList<SyncedMix> SyncedMixes()
    {
        lock (_lock)
        {
            return
            [
                .. _mixes.Select(m => new SyncedMix(m.Id, m, ModifiedAtLocked(m.Id))),
                .. _removed.Select(r => new SyncedMix(r.Key, null, r.Value)),
            ];
        }
    }

    /// <summary>Take in another device's mixes: each one changed there later than here replaces
    /// this one (a deletion too). Raises <see cref="Changed"/> once if anything changed, and
    /// returns whether it did. The caller saves.</summary>
    public bool MergeSynced(IEnumerable<SyncedMix> remote)
    {
        ArgumentNullException.ThrowIfNull(remote);
        bool changed;
        lock (_lock) changed = MergeLocked(remote);
        if (changed) Changed?.Invoke();
        return changed;
    }

    private bool MergeLocked(IEnumerable<SyncedMix> remote)
    {
        var changed = false;
        foreach (var theirs in remote)
        {
            if (string.IsNullOrWhiteSpace(theirs.Id)) continue;
            var known = _modified.ContainsKey(theirs.Id) || _removed.ContainsKey(theirs.Id) || Find(theirs.Id) != null;
            if (known && !Newer(theirs)) continue;
            if (theirs.Mix is { } mix)
            {
                var i = _mixes.FindIndex(m => m.Id == theirs.Id);
                if (i >= 0) _mixes[i] = mix with { Id = theirs.Id };
                else _mixes.Add(mix with { Id = theirs.Id });
                _modified[theirs.Id] = theirs.Modified;
                _removed.Remove(theirs.Id);
                changed = true;
            }
            else
            {
                changed |= _mixes.RemoveAll(m => m.Id == theirs.Id) > 0;
                _modified.Remove(theirs.Id);
                _removed[theirs.Id] = theirs.Modified;
            }
        }
        return changed;
    }

    private bool Newer(SyncedMix theirs)
    {
        var mine = ModifiedAtLocked(theirs.Id);
        if (theirs.Modified != mine) return theirs.Modified > mine;
        var mineRemoved = _removed.ContainsKey(theirs.Id);
        if ((theirs.Mix == null) != mineRemoved) return theirs.Mix == null; // a tie: the deletion
        return string.CompareOrdinal(theirs.Mix?.Name, Find(theirs.Id)?.Name) > 0;
    }

    private DateTimeOffset Now()
    {
        var now = Clock.GetUtcNow();
        return now.AddTicks(-(now.Ticks % TimeSpan.TicksPerMillisecond));
    }

    private static void Log(string msg) => Trace.WriteLine($"[hzn-mixes] {msg}");

    /// <summary>The file this store was loaded from, and saves to; null for one kept in memory.</summary>
    public string? FilePath { get; private set; }

    /// <summary>Loads the store kept at <paramref name="path"/> (an empty one when there's no file),
    /// which <see cref="SaveToDisk"/> then saves to.</summary>
    public static MixStore LoadFromDisk(string path)
    {
        var store = new MixStore { FilePath = path };
        try
        {
            if (!File.Exists(path)) return store;

            using var stream = File.OpenRead(path);
            using var doc = JsonDocument.Parse(stream);
            var root = doc.RootElement;

            if (root.TryGetProperty("mixes", out var arr) && arr.ValueKind == JsonValueKind.Array)
            {
                foreach (var item in arr.EnumerateArray())
                {
                    var mix = TryReadMix(item);
                    if (mix == null) continue;
                    store._mixes.Add(mix);
                    if (ReadTime(item) is { } modified) store._modified[mix.Id] = modified;
                    if (ReadString(item, "station") is { Length: > 0 } station)
                        store._legacyStations[mix.Id] = station;
                }
            }
            if (root.TryGetProperty("removed", out var removed) && removed.ValueKind == JsonValueKind.Array)
            {
                foreach (var item in removed.EnumerateArray())
                {
                    if (item.ValueKind == JsonValueKind.Object && ReadString(item, "id") is { Length: > 0 } id && ReadTime(item) is { } at)
                        store._removed[id] = at;
                }
            }
        }
        catch (Exception ex)
        {
            Log($"load failed (using empty store): {ex.Message}");
        }
        return store;
    }

    /// <summary>Saves to <paramref name="path"/>, else the file it was loaded from; a store kept in
    /// memory (built with <c>new</c>) saves nowhere.</summary>
    public void SaveToDisk(string? path = null)
    {
        path ??= FilePath;
        if (path == null) return;
        try
        {
            lock (_lock) AtomicFile.Write(path, stream =>
            {
                using var writer = new Utf8JsonWriter(stream, new JsonWriterOptions { Indented = true });

                writer.WriteStartObject();
                writer.WriteStartArray("mixes");
                foreach (var m in _mixes)
                {
                    writer.WriteStartObject();
                    writer.WriteString("id", m.Id);
                    writer.WriteString("name", m.Name);
                    if (_modified.TryGetValue(m.Id, out var modified)) writer.WriteString("modified", FormatTime(modified));

                    writer.WriteStartArray("entries");
                    foreach (var e in m.Entries)
                    {
                        writer.WriteStartObject();
                        writer.WriteString("source", e.SourceId);
                        writer.WriteString("locator", e.Locator);
                        if (e.DisplayName != null) writer.WriteString("name", e.DisplayName);
                        writer.WriteEndObject();
                    }
                    writer.WriteEndArray();

                    writer.WriteEndObject();
                }
                writer.WriteEndArray();
                if (_removed.Count > 0)
                {
                    writer.WriteStartArray("removed");
                    foreach (var (id, at) in _removed)
                    {
                        writer.WriteStartObject();
                        writer.WriteString("id", id);
                        writer.WriteString("modified", FormatTime(at));
                        writer.WriteEndObject();
                    }
                    writer.WriteEndArray();
                }
                writer.WriteEndObject();
            });
        }
        catch (Exception ex)
        {
            Log($"save failed: {ex.Message}");
        }
    }

    private static Mix? TryReadMix(JsonElement item)
    {
        if (item.ValueKind != JsonValueKind.Object) return null;

        var id = ReadString(item, "id");
        var name = ReadString(item, "name");
        if (string.IsNullOrEmpty(id) || string.IsNullOrEmpty(name)) return null;


        var entries = new List<ContentRef>();
        if (item.TryGetProperty("entries", out var arr) && arr.ValueKind == JsonValueKind.Array)
        {
            foreach (var e in arr.EnumerateArray())
            {
                if (e.ValueKind != JsonValueKind.Object) continue;
                var source = ReadString(e, "source");
                var locator = ReadString(e, "locator");
                if (string.IsNullOrEmpty(source) || string.IsNullOrEmpty(locator)) continue;
                entries.Add(new ContentRef(source!, locator!, ReadString(e, "name")));
            }
        }

        return new Mix(id!, name!, entries);
    }

    private static string? ReadString(JsonElement e, string key)
        => e.TryGetProperty(key, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    private static DateTimeOffset? ReadTime(JsonElement e) =>
        ReadString(e, "modified") is { } text
        && DateTimeOffset.TryParse(text, System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.RoundtripKind, out var at)
            ? at
            : null;

    private static string FormatTime(DateTimeOffset at) =>
        at.UtcDateTime.ToString("yyyy-MM-ddTHH:mm:ss.fffZ", System.Globalization.CultureInfo.InvariantCulture);
}

/// <summary>A mix as it goes between your devices: the mix (null when it was deleted) and when it
/// last changed.</summary>
public sealed record SyncedMix(string Id, Mix? Mix, DateTimeOffset Modified);
