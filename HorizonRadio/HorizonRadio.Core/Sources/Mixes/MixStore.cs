using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.Json;
using HorizonRadio.Core.IO;

namespace HorizonRadio.Core.Sources.Mixes;

/// <summary>
/// Persists the user's mixes to <c>%LOCALAPPDATA%\HorizonRadio\mixes.json</c>,
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
///       "id": "…", "name": "Drive",
///       "entries": [
///         { "source": "youtube", "locator": "https://…", "name": "Synthwave mix" },
///         { "source": "local",   "locator": "C:\\Music\\foo.flac" }
///       ]
///     }
///   ]
/// }
/// </code>
/// </summary>
public sealed class MixStore
{
    private readonly List<Mix> _mixes = new();

    /// <summary>Raised after any add/update/remove, on the caller's thread.</summary>
    public event Action? Changed;

    public IReadOnlyList<Mix> All => _mixes;

    private readonly Dictionary<string, string> _legacyStations = new(StringComparer.Ordinal);

    /// <summary>
    /// LEGACY: mix id → the in-game station a mix used to override, read from pre-plugin files
    /// (<c>"station"</c> on a mix). No longer written; <c>LegacySettingsMigration</c> moves these
    /// into the FH6 plugin's station assignments.
    /// </summary>
    public IReadOnlyDictionary<string, string> LegacyStations => _legacyStations;

    public Mix? Get(string id) => _mixes.FirstOrDefault(m => m.Id == id);

    /// <summary>Insert a new mix or replace the one with the same id, then notify.</summary>
    public void AddOrUpdate(Mix mix)
    {
        var i = _mixes.FindIndex(m => m.Id == mix.Id);
        if (i >= 0) _mixes[i] = mix;
        else _mixes.Add(mix);
        Changed?.Invoke();
    }

    public void Remove(string id)
    {
        if (_mixes.RemoveAll(m => m.Id == id) == 0) return;
        Changed?.Invoke();
    }

    private static string DefaultPath =>
        AppPaths.In("mixes.json");

    private static void Log(string msg) => Trace.WriteLine($"[hzn-mixes] {msg}");

    public static MixStore LoadFromDisk(string? path = null)
    {
        path ??= DefaultPath;
        var store = new MixStore();
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
                    if (ReadString(item, "station") is { Length: > 0 } station)
                        store._legacyStations[mix.Id] = station;
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
        try
        {
            AtomicFile.Write(path, stream =>
            {
                using var writer = new Utf8JsonWriter(stream, new JsonWriterOptions { Indented = true });

                writer.WriteStartObject();
                writer.WriteStartArray("mixes");
                foreach (var m in _mixes)
                {
                    writer.WriteStartObject();
                    writer.WriteString("id", m.Id);
                    writer.WriteString("name", m.Name);

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
}
