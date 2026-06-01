using System.Diagnostics;
using System.Text.Json;
using HorizonRadio.Core.Sources.Config;

namespace HorizonRadio.Core.Metadata;

/// <summary>
/// Persists the user's metadata-provider choice + per-provider config
/// to <c>%LOCALAPPDATA%\HorizonRadio\metadata-config.json</c>. Same
/// shape as <see cref="SourceConfigStore"/> but lives in its own file
/// because credentials (Spotify client secret) should be obvious in
/// the filesystem, not tucked into sources.json.
/// </summary>
public sealed class MetadataConfigStore
{
    public string? SelectedProviderId { get; set; }

    private readonly Dictionary<string, Dictionary<string, object?>> _perProvider = new();

    private static string DefaultPath =>
        Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "HorizonRadio",
            "metadata-config.json");

    private static void Log(string msg) => Debug.WriteLine($"[hzn-meta-cfg] {msg}");

    public ConfigValues Load(string providerId, IReadOnlyList<ConfigField> schema)
    {
        var values = new ConfigValues();
        if (_perProvider.TryGetValue(providerId, out var stored))
        {
            foreach (var (k, v) in stored) values.Set(k, v);
        }
        values.ApplyDefaults(schema);
        return values;
    }

    public void Save(string providerId, ConfigValues values)
    {
        _perProvider[providerId] = new Dictionary<string, object?>(values.AsReadOnly());
    }

    public static MetadataConfigStore LoadFromDisk(string? path = null)
    {
        path ??= DefaultPath;
        var store = new MetadataConfigStore();
        try
        {
            if (!File.Exists(path)) return store;
            using var stream = File.OpenRead(path);
            using var doc = JsonDocument.Parse(stream);
            var root = doc.RootElement;

            if (root.TryGetProperty("selected", out var sel) && sel.ValueKind == JsonValueKind.String)
                store.SelectedProviderId = sel.GetString();

            if (root.TryGetProperty("perProvider", out var per) && per.ValueKind == JsonValueKind.Object)
            {
                foreach (var prov in per.EnumerateObject())
                {
                    if (prov.Value.ValueKind != JsonValueKind.Object) continue;
                    var bag = new Dictionary<string, object?>();
                    foreach (var prop in prov.Value.EnumerateObject())
                        bag[prop.Name] = JsonToObject(prop.Value);
                    store._perProvider[prov.Name] = bag;
                }
            }
        }
        catch (Exception ex) { Log($"load failed: {ex.Message}"); }
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
            if (SelectedProviderId != null) writer.WriteString("selected", SelectedProviderId);
            writer.WriteStartObject("perProvider");
            foreach (var (provId, bag) in _perProvider)
            {
                writer.WriteStartObject(provId);
                foreach (var (k, v) in bag) WriteValue(writer, k, v);
                writer.WriteEndObject();
            }
            writer.WriteEndObject();
            writer.WriteEndObject();
        }
        catch (Exception ex) { Log($"save failed: {ex.Message}"); }
    }

    private static object? JsonToObject(JsonElement el) => el.ValueKind switch
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
