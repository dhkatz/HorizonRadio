using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text.Json;

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
/// Storage uses <see cref="object"/> values to round-trip JSON
/// primitives (string/bool/number); <see cref="ConfigValues"/>'s typed
/// accessors handle the boxing.
/// </summary>
public sealed class SourceConfigStore
{
    public string? LastSelectedId { get; set; }

    /// <summary>Which in-game radio station Horizon Radio replaces (null/Any
    /// = whatever's active). Persisted so the choice survives restarts.</summary>
    public string? TargetStation { get; set; }

    /// <summary>Global shuffle preference (applies to whichever source is
    /// active). Persisted so the choice survives restarts.</summary>
    public bool Shuffle { get; set; }

    /// <summary>Whether to also play the active source's audio out of a local
    /// speaker ("test playback"), independent of the in-game pipe.</summary>
    public bool PreviewEnabled { get; set; }

    /// <summary>Render endpoint id for preview playback (null = system
    /// default). Matches <c>MMDevice.ID</c>.</summary>
    public string? PreviewDeviceId { get; set; }

    /// <summary>Preview playback volume in [0, 1].</summary>
    public double PreviewVolume { get; set; } = 1.0;

    /// <summary>Source ids in the user's preferred order for unified search — the default
    /// "Play" on a merged result uses the highest-priority source it has (the per-result
    /// picker overrides per row). Empty = fall back to catalog order. Persisted so the
    /// choice survives restarts.</summary>
    public List<string> SearchSourcePriority { get; set; } = new();

    private readonly Dictionary<string, Dictionary<string, object?>> _perSource = new();

    private static string DefaultPath =>
        Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "HorizonRadio",
            "sources.json");

    private static void Log(string msg) => Debug.WriteLine($"[hzn-cfg] {msg}");

    /// <summary>Read all values previously stored for <paramref name="sourceId"/>.
    /// Returns a fresh <see cref="ConfigValues"/> populated with schema defaults
    /// if no entry exists yet, so callers always get a usable bag.</summary>
    public ConfigValues Load(string sourceId, IReadOnlyList<ConfigField> schema)
    {
        var values = new ConfigValues();
        if (_perSource.TryGetValue(sourceId, out var stored))
        {
            foreach (var (k, v) in stored) values.Set(k, v);
        }
        values.ApplyDefaults(schema);
        return values;
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
                        bag[prop.Name] = JsonElementToObject(prop.Value);
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
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);

            using var stream = File.Create(path);
            using var writer = new Utf8JsonWriter(stream, new JsonWriterOptions { Indented = true });

            writer.WriteStartObject();
            if (LastSelectedId != null) writer.WriteString("lastSelected", LastSelectedId);
            if (TargetStation != null) writer.WriteString("targetStation", TargetStation);
            writer.WriteBoolean("shuffle", Shuffle);
            writer.WriteBoolean("previewEnabled", PreviewEnabled);
            if (PreviewDeviceId != null) writer.WriteString("previewDeviceId", PreviewDeviceId);
            writer.WriteNumber("previewVolume", PreviewVolume);
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
                foreach (var (k, v) in bag) WriteValue(writer, k, v);
                writer.WriteEndObject();
            }
            writer.WriteEndObject();
            writer.WriteEndObject();
        }
        catch (Exception ex)
        {
            Log($"save failed: {ex.Message}");
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
