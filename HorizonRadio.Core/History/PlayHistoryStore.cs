using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.Json;

namespace HorizonRadio.Core.History;

/// <summary>
/// Persists the play history to <c>%LOCALAPPDATA%\HorizonRadio\history.json</c>, in the same
/// hand-editable JSON style as the other stores. Entries are kept newest-first and capped at
/// <see cref="MaxEntries"/> (oldest evicted), so the file stays small — no album art is stored
/// (the list re-enriches art on demand). Mutations raise <see cref="Changed"/> so the History
/// tab stays in sync; saving is the caller's call via <see cref="SaveToDisk"/>
/// (<see cref="PlayHistoryService"/> debounces it).
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
        lock (_lock) removed = _entries.RemoveAll(e => e.Id == id) > 0;
        if (removed) Changed?.Invoke();
    }

    public void Clear()
    {
        bool any;
        lock (_lock) { any = _entries.Count > 0; _entries.Clear(); }
        if (any) Changed?.Invoke();
    }

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
        Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "HorizonRadio",
            "history.json");

    private static void Log(string msg) => Debug.WriteLine($"[hzn-history] {msg}");

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
                foreach (var e in _entries)
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
                writer.WriteEndArray();
                writer.WriteEndObject();
            }
            bytes = ms.ToArray();
        }

        // Write to a unique temp then atomically rename, so a crash or a concurrent save can never
        // leave a half-written/truncated history.json (a plain File.Create truncates in place).
        var tmp = $"{path}.{Guid.NewGuid():n}.tmp";
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllBytes(tmp, bytes);
            File.Move(tmp, path, overwrite: true);
        }
        catch (Exception ex)
        {
            Log($"save failed: {ex.Message}");
            try { if (File.Exists(tmp)) File.Delete(tmp); } catch { /* best effort */ }
        }
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
