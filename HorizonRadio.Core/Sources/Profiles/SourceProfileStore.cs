using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.Json;

namespace HorizonRadio.Core.Sources.Profiles;

/// <summary>
/// Persists the user's source profiles to
/// <c>%LOCALAPPDATA%\HorizonRadio\profiles.json</c>. Same hand-editable JSON
/// style as <see cref="Config.SourceConfigStore"/>. CRUD mutations raise
/// <see cref="Changed"/> so the Profiles tab and the Now Playing quick-switch
/// stay in sync off one store instance. Saving is left to the caller (as with
/// the other stores), via <see cref="SaveToDisk"/>.
///
/// File shape:
/// <code>
/// {
///   "profiles": [
///     { "id": "…", "name": "Chill", "source": "youtube", "content": { "url": "…" } }
///   ]
/// }
/// </code>
/// </summary>
public sealed class SourceProfileStore
{
    private readonly List<SourceProfile> _profiles = new();

    /// <summary>Raised after any add/update/remove. Fires on the caller's thread
    /// (the UI thread for tab edits).</summary>
    public event Action? Changed;

    public IReadOnlyList<SourceProfile> All => _profiles;

    public SourceProfile? Get(string id) => _profiles.FirstOrDefault(p => p.Id == id);

    /// <summary>Insert a new profile or replace the existing one with the same id,
    /// then notify.</summary>
    public void AddOrUpdate(SourceProfile profile)
    {
        var i = _profiles.FindIndex(p => p.Id == profile.Id);
        if (i >= 0) _profiles[i] = profile;
        else _profiles.Add(profile);
        Changed?.Invoke();
    }

    public void Remove(string id)
    {
        if (_profiles.RemoveAll(p => p.Id == id) == 0) return;
        Changed?.Invoke();
    }

    private static string DefaultPath =>
        Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "HorizonRadio",
            "profiles.json");

    private static void Log(string msg) => Debug.WriteLine($"[hzn-profiles] {msg}");

    public static SourceProfileStore LoadFromDisk(string? path = null)
    {
        path ??= DefaultPath;
        var store = new SourceProfileStore();
        try
        {
            if (!File.Exists(path)) return store;

            using var stream = File.OpenRead(path);
            using var doc = JsonDocument.Parse(stream);
            var root = doc.RootElement;

            if (root.TryGetProperty("profiles", out var arr) && arr.ValueKind == JsonValueKind.Array)
            {
                foreach (var item in arr.EnumerateArray())
                {
                    if (item.ValueKind != JsonValueKind.Object) continue;
                    var id = item.TryGetProperty("id", out var i) && i.ValueKind == JsonValueKind.String ? i.GetString() : null;
                    var name = item.TryGetProperty("name", out var n) && n.ValueKind == JsonValueKind.String ? n.GetString() : null;
                    var source = item.TryGetProperty("source", out var s) && s.ValueKind == JsonValueKind.String ? s.GetString() : null;
                    if (string.IsNullOrEmpty(id) || string.IsNullOrEmpty(name) || string.IsNullOrEmpty(source)) continue;

                    var content = new Dictionary<string, object?>();
                    if (item.TryGetProperty("content", out var c) && c.ValueKind == JsonValueKind.Object)
                        foreach (var prop in c.EnumerateObject())
                            content[prop.Name] = JsonElementToObject(prop.Value);

                    store._profiles.Add(new SourceProfile(id!, name!, source!, content));
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
            writer.WriteStartArray("profiles");
            foreach (var p in _profiles)
            {
                writer.WriteStartObject();
                writer.WriteString("id", p.Id);
                writer.WriteString("name", p.Name);
                writer.WriteString("source", p.SourceId);
                writer.WriteStartObject("content");
                foreach (var (k, v) in p.Content) WriteValue(writer, k, v);
                writer.WriteEndObject();
                writer.WriteEndObject();
            }
            writer.WriteEndArray();
            writer.WriteEndObject();
        }
        catch (Exception ex)
        {
            Log($"save failed: {ex.Message}");
        }
    }

    // JSON primitive round-tripping — mirrors SourceConfigStore so a profile's
    // content bag stores the same string/bool/number shapes ConfigValues expects.
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
