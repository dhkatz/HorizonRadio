using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.Json;
using HorizonRadio.Core.IO;
using HorizonRadio.Sdk.IO;

namespace HorizonRadio.Core.History;

/// <summary>
/// Persists the play history to <c>%LOCALAPPDATA%\HorizonRadio\history.json</c>, in the same
/// hand-editable JSON style as the other stores. Entries are kept newest-first and capped at
/// <see cref="MaxEntries"/> (oldest evicted), so the file stays small — no album art is stored
/// (the list re-enriches art on demand). Mutations raise <see cref="Changed"/> so the History
/// tab stays in sync; saving is the caller's call via <see cref="SaveToDisk"/>
/// (<see cref="PlayHistoryService"/> debounces it). A song removed, and when the history was last
/// cleared, are kept too (<c>"removed"</c>, <c>"clearedBefore"</c>), so your devices can keep their
/// histories in step (<see cref="SyncedHistory"/>, <see cref="MergeSynced"/>).
///
/// File shape:
/// <code>
/// {
///   "entries": [
///     {
///       "id": "…", "playedAt": "2026-06-16T12:00:00+00:00",
///       "title": "…", "artist": "…", "album": null, "year": null,
///       "sourceId": "radio", "sourceDisplay": "Internet Radio",
///       "matchState": "Unmatched",
///       "sources": [ { "sourceId": "youtube", "display": "YouTube", "locator": "https://…" } ],
///       "candidates": [ { "artist": "…", "title": "…" } ]
///     }
///   ]
/// }
/// </code>
/// </summary>
public sealed class PlayHistoryStore
{
    /// <summary>How many songs we remember. ~1 KB/entry text-only keeps the file well under 1 MB.</summary>
    public const int MaxEntries = 1000;

    private readonly object _lock = new();
    private readonly List<PlayHistoryEntry> _entries = new(); // newest first
    private readonly Dictionary<string, DateTimeOffset> _removed = new(StringComparer.Ordinal); // id → when
    private DateTimeOffset? _clearedBefore;

    /// <summary>How many removed songs are remembered (so another device doesn't bring them back).</summary>
    private const int MaxRemoved = 2000;

    /// <summary>Raised after any add/remove/clear/match-state change, on the mutating thread.</summary>
    public event Action? Changed;

    /// <summary>A snapshot of the entries, newest first.</summary>
    public IReadOnlyList<PlayHistoryEntry> All
    {
        get { lock (_lock) return _entries.ToList(); }
    }

    /// <summary>Prepend a freshly played song, evicting the oldest past the cap.</summary>
    public void Add(PlayHistoryEntry entry)
    {
        lock (_lock)
        {
            _entries.Insert(0, entry);
            if (_entries.Count > MaxEntries)
                _entries.RemoveRange(MaxEntries, _entries.Count - MaxEntries);
        }
        Changed?.Invoke();
    }

    public void Remove(string id)
    {
        bool removed;
        lock (_lock)
        {
            removed = _entries.RemoveAll(e => e.Id == id) > 0;
            if (removed) RememberRemovedLocked(id, DateTimeOffset.UtcNow);
        }
        if (removed) Changed?.Invoke();
    }

    public void Clear()
    {
        bool any;
        lock (_lock)
        {
            any = _entries.Count > 0;
            _entries.Clear();
            _removed.Clear();
            _clearedBefore = DateTimeOffset.UtcNow;
        }
        if (any) Changed?.Invoke();
    }

    /// <summary>The history as another of your devices needs it: the songs (all, or the
    /// <paramref name="newest"/>), the ones removed, and when it was last cleared.</summary>
    public SyncedHistory SyncedHistory(int? newest = null)
    {
        lock (_lock)
        {
            return new SyncedHistory(
                [.. _entries.Take(newest ?? _entries.Count)],
                [.. _removed.Keys],
                _clearedBefore);
        }
    }

    /// <summary>
    /// Take in another device's history: songs not here (and not removed here) are added in the
    /// order they were played, songs removed there go, and a later clearing there drops what was
    /// played before it. A song already here stays as it is. Raises <see cref="Changed"/> once if
    /// anything changed, and returns whether it did.
    /// </summary>
    public bool MergeSynced(SyncedHistory theirs)
    {
        ArgumentNullException.ThrowIfNull(theirs);
        var changed = false;
        lock (_lock)
        {
            if (theirs.ClearedBefore is { } cleared && (_clearedBefore == null || cleared > _clearedBefore))
            {
                _clearedBefore = cleared;
                changed |= _entries.RemoveAll(e => e.PlayedAt < cleared) > 0;
            }
            foreach (var id in theirs.Removed)
            {
                if (string.IsNullOrEmpty(id) || _removed.ContainsKey(id)) continue;
                RememberRemovedLocked(id, DateTimeOffset.UtcNow);
                changed |= _entries.RemoveAll(e => e.Id == id) > 0;
            }
            var known = _entries.Select(e => e.Id).ToHashSet(StringComparer.Ordinal);
            var added = false;
            foreach (var entry in theirs.Entries)
            {
                if (_removed.ContainsKey(entry.Id) || (_clearedBefore is { } before && entry.PlayedAt < before) || !known.Add(entry.Id)) continue;
                _entries.Add(entry);
                added = true;
            }
            if (added)
            {
                _entries.Sort((a, b) => b.PlayedAt.CompareTo(a.PlayedAt));
                if (_entries.Count > MaxEntries) _entries.RemoveRange(MaxEntries, _entries.Count - MaxEntries);
                changed = true;
            }
        }
        if (changed) Changed?.Invoke();
        return changed;
    }

    private void RememberRemovedLocked(string id, DateTimeOffset at)
    {
        _removed[id] = at;
        if (_removed.Count <= MaxRemoved) return;
        foreach (var oldest in _removed.OrderBy(r => r.Value).Take(_removed.Count - MaxRemoved).Select(r => r.Key).ToList())
            _removed.Remove(oldest);
    }

    /// <summary>A song as JSON, as the file keeps it.</summary>
    internal static JsonElement ToJson(PlayHistoryEntry entry)
    {
        using var ms = new MemoryStream();
        using (var writer = new Utf8JsonWriter(ms)) WriteEntry(writer, entry);
        return JsonDocument.Parse(ms.ToArray()).RootElement.Clone();
    }

    /// <summary>A song from JSON, or null when it isn't one.</summary>
    internal static PlayHistoryEntry? FromJson(JsonElement json) => TryReadEntry(json);

    /// <summary>Update one entry's identification verdict once its catalog lookup resolves.
    /// No-op (no event) if the entry is gone or the state is unchanged.</summary>
    public void SetMatchState(string id, HistoryMatchState state)
    {
        bool changed = false;
        lock (_lock)
        {
            var e = _entries.FirstOrDefault(x => x.Id == id);
            if (e != null && e.MatchState != state) { e.MatchState = state; changed = true; }
        }
        if (changed) Changed?.Invoke();
    }

    /// <summary>Store the playable sources found for a (freeform) entry. No-op (no event) if the
    /// entry is gone or the set is unchanged — so a re-resolve that finds the same (or still no)
    /// sources doesn't churn the UI or re-trigger a save.</summary>
    public void SetSources(string id, IReadOnlyList<ReplaySource> sources)
    {
        bool changed = false;
        lock (_lock)
        {
            var e = _entries.FirstOrDefault(x => x.Id == id);
            if (e != null && !e.Sources.SequenceEqual(sources)) { e.Sources = sources; changed = true; }
        }
        if (changed) Changed?.Invoke();
    }

    private static string DefaultPath =>
        AppPaths.In("history.json");

    private static void Log(string msg) => Trace.WriteLine($"[hzn-history] {msg}");

    public static PlayHistoryStore LoadFromDisk(string? path = null)
    {
        path ??= DefaultPath;
        var store = new PlayHistoryStore();
        try
        {
            if (!File.Exists(path)) return store;

            using var stream = File.OpenRead(path);
            using var doc = JsonDocument.Parse(stream);
            var root = doc.RootElement;

            if (root.TryGetProperty("entries", out var arr) && arr.ValueKind == JsonValueKind.Array)
            {
                foreach (var item in arr.EnumerateArray())
                {
                    var entry = TryReadEntry(item);
                    if (entry != null) store._entries.Add(entry);
                }
                // Defend against a hand-edited file growing past the cap.
                if (store._entries.Count > MaxEntries)
                    store._entries.RemoveRange(MaxEntries, store._entries.Count - MaxEntries);
            }
            if (root.TryGetProperty("removed", out var removed) && removed.ValueKind == JsonValueKind.Array)
            {
                foreach (var item in removed.EnumerateArray())
                {
                    if (ReadString(item, "id") is { Length: > 0 } id
                        && DateTimeOffset.TryParse(ReadString(item, "at"), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var at))
                        store._removed[id] = at;
                }
            }
            if (DateTimeOffset.TryParse(ReadString(root, "clearedBefore"), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var cleared))
                store._clearedBefore = cleared;
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

        // Serialize INSIDE the lock: entries are mutated in place (SetSources/SetMatchState), so
        // reading their fields outside the lock would race a concurrent writer. Building the JSON
        // is CPU-only (no I/O), so holding the lock is brief; the file write happens after release.
        byte[] bytes;
        lock (_lock)
        {
            using var ms = new MemoryStream();
            using (var writer = new Utf8JsonWriter(ms, new JsonWriterOptions { Indented = true }))
            {
                writer.WriteStartObject();
                writer.WriteStartArray("entries");
                foreach (var e in _entries) WriteEntry(writer, e);
                writer.WriteEndArray();
                if (_removed.Count > 0)
                {
                    writer.WriteStartArray("removed");
                    foreach (var (id, at) in _removed)
                    {
                        writer.WriteStartObject();
                        writer.WriteString("id", id);
                        writer.WriteString("at", at.ToString("o", CultureInfo.InvariantCulture));
                        writer.WriteEndObject();
                    }
                    writer.WriteEndArray();
                }
                if (_clearedBefore is { } cleared) writer.WriteString("clearedBefore", cleared.ToString("o", CultureInfo.InvariantCulture));
                writer.WriteEndObject();
            }
            bytes = ms.ToArray();
        }

        // Write to a unique temp then atomically rename, so a crash or a concurrent save can never
        // leave a half-written/truncated history.json (a plain File.Create truncates in place).
        try
        {
            AtomicFile.WriteAllBytes(path, bytes);
        }
        catch (Exception ex)
        {
            Log($"save failed: {ex.Message}");
        }
    }

    private static void WriteEntry(Utf8JsonWriter writer, PlayHistoryEntry e)
    {
        writer.WriteStartObject();
        writer.WriteString("id", e.Id);
        writer.WriteString("playedAt", e.PlayedAt.ToString("o", CultureInfo.InvariantCulture));
        writer.WriteString("title", e.Title);
        writer.WriteString("artist", e.Artist);
        if (e.Album != null) writer.WriteString("album", e.Album);
        if (e.Year is { } y) writer.WriteNumber("year", y);
        writer.WriteString("sourceId", e.SourceId);
        writer.WriteString("sourceDisplay", e.SourceDisplay);
        writer.WriteString("matchState", e.MatchState.ToString());
        if (e.Sources.Count > 0)
        {
            writer.WriteStartArray("sources");
            foreach (var s in e.Sources)
            {
                writer.WriteStartObject();
                writer.WriteString("sourceId", s.SourceId);
                writer.WriteString("display", s.SourceDisplay);
                writer.WriteString("locator", s.Locator);
                writer.WriteEndObject();
            }
            writer.WriteEndArray();
        }
        if (e.Candidates.Count > 0)
        {
            writer.WriteStartArray("candidates");
            foreach (var c in e.Candidates)
            {
                writer.WriteStartObject();
                if (c.Artist != null) writer.WriteString("artist", c.Artist);
                writer.WriteString("title", c.Title);
                writer.WriteEndObject();
            }
            writer.WriteEndArray();
        }
        writer.WriteEndObject();
    }

    private static PlayHistoryEntry? TryReadEntry(JsonElement item)
    {
        if (item.ValueKind != JsonValueKind.Object) return null;

        var id = ReadString(item, "id");
        var title = ReadString(item, "title");
        if (string.IsNullOrEmpty(id) || string.IsNullOrEmpty(title)) return null;

        if (!item.TryGetProperty("playedAt", out var pa) || pa.ValueKind != JsonValueKind.String ||
            !DateTimeOffset.TryParse(pa.GetString(), CultureInfo.InvariantCulture,
                DateTimeStyles.RoundtripKind, out var playedAt))
            return null;

        var sourceId = ReadString(item, "sourceId") ?? "";

        var sources = new List<ReplaySource>();
        if (item.TryGetProperty("sources", out var srcs) && srcs.ValueKind == JsonValueKind.Array)
        {
            foreach (var s in srcs.EnumerateArray())
            {
                if (s.ValueKind != JsonValueKind.Object) continue;
                var sid = ReadString(s, "sourceId");
                var loc = ReadString(s, "locator");
                if (!string.IsNullOrEmpty(sid) && !string.IsNullOrEmpty(loc))
                    sources.Add(new ReplaySource(sid!, ReadString(s, "display") ?? sid!, loc!));
            }
        }
        else if (ReadString(item, "replaySourceId") is { } legacySid && ReadString(item, "replayLocator") is { } legacyLoc)
        {
            // Pre-multi-source entries stored a single (replaySourceId, replayLocator).
            sources.Add(new ReplaySource(legacySid, ReadString(item, "sourceDisplay") ?? legacySid, legacyLoc));
        }

        var candidates = new List<HistoryCandidate>();
        if (item.TryGetProperty("candidates", out var ca) && ca.ValueKind == JsonValueKind.Array)
        {
            foreach (var c in ca.EnumerateArray())
            {
                if (c.ValueKind != JsonValueKind.Object) continue;
                var ct = ReadString(c, "title");
                if (!string.IsNullOrEmpty(ct)) candidates.Add(new HistoryCandidate(ReadString(c, "artist"), ct!));
            }
        }

        return new PlayHistoryEntry
        {
            Id = id!,
            PlayedAt = playedAt,
            Title = title!,
            Artist = ReadString(item, "artist") ?? "",
            Album = ReadString(item, "album"),
            Year = item.TryGetProperty("year", out var yr) && yr.ValueKind == JsonValueKind.Number ? yr.GetInt32() : null,
            SourceId = sourceId,
            SourceDisplay = ReadString(item, "sourceDisplay") ?? sourceId,
            MatchState = Enum.TryParse<HistoryMatchState>(ReadString(item, "matchState"), out var ms) ? ms : HistoryMatchState.Unknown,
            Sources = sources,
            Candidates = candidates,
        };
    }

    private static string? ReadString(JsonElement e, string key)
        => e.TryGetProperty(key, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
}

/// <summary>A play history as it goes between your devices: songs, the ids of songs removed, and
/// when it was last cleared (everything played before then is gone).</summary>
public sealed record SyncedHistory(IReadOnlyList<PlayHistoryEntry> Entries, IReadOnlyList<string> Removed, DateTimeOffset? ClearedBefore);
