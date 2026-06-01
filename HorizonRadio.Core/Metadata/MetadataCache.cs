using System.Collections.Concurrent;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace HorizonRadio.Core.Metadata;

/// <summary>
/// On-disk cache for <see cref="IMetadataEnricher"/> results, keyed
/// by a stable hash of (enricher-id, query). Stops us from hammering
/// MusicBrainz when a track repeats. Stored under
/// <c>%LOCALAPPDATA%\HorizonRadio\metadata\</c> as one file per cache
/// key (so partial failure doesn't corrupt the whole cache, and
/// inspecting / nuking individual entries is easy).
///
/// Album art is the cache's primary payload — it's the biggest single
/// thing a Track grows, and it doesn't change for a given recording.
/// Title / artist / album text is cached alongside for completeness.
/// </summary>
public sealed class MetadataCache
{
    private readonly string _root;
    private readonly ConcurrentDictionary<string, Entry?> _memoryCache = new();

    public sealed record Entry(
        string? Title,
        string? Artist,
        string? Album,
        byte[]? AlbumArt,
        string? Mbid);

    public MetadataCache(string? root = null)
    {
        _root = root ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "HorizonRadio", "metadata");
        Directory.CreateDirectory(_root);
    }

    private static void Log(string msg) => Debug.WriteLine($"[hzn-cache] {msg}");

    public static string Key(string enricherId, string query)
    {
        var bytes = Encoding.UTF8.GetBytes($"{enricherId}:{query}");
        var hash = SHA256.HashData(bytes);
        // Hex prefix is enough — full SHA-256 hex is overkill for path safety.
        return Convert.ToHexString(hash, 0, 16);
    }

    private string PathFor(string key) =>
        Path.Combine(_root, key + ".json");

    public Entry? TryGet(string key)
    {
        if (_memoryCache.TryGetValue(key, out var cached)) return cached;

        var path = PathFor(key);
        if (!File.Exists(path))
        {
            _memoryCache[key] = null;   // negative result; avoid re-hitting disk
            return null;
        }

        try
        {
            using var stream = File.OpenRead(path);
            using var doc = JsonDocument.Parse(stream);
            var r = doc.RootElement;
            var entry = new Entry(
                Title: GetString(r, "title"),
                Artist: GetString(r, "artist"),
                Album: GetString(r, "album"),
                AlbumArt: GetBase64(r, "art_b64"),
                Mbid: GetString(r, "mbid"));
            _memoryCache[key] = entry;
            return entry;
        }
        catch (Exception ex)
        {
            Log($"read {key}: {ex.Message}");
            return null;
        }
    }

    public void Put(string key, Entry entry)
    {
        _memoryCache[key] = entry;

        try
        {
            using var stream = File.Create(PathFor(key));
            using var writer = new Utf8JsonWriter(stream, new JsonWriterOptions { Indented = false });
            writer.WriteStartObject();
            if (entry.Title != null) writer.WriteString("title", entry.Title);
            if (entry.Artist != null) writer.WriteString("artist", entry.Artist);
            if (entry.Album != null) writer.WriteString("album", entry.Album);
            if (entry.Mbid != null) writer.WriteString("mbid", entry.Mbid);
            if (entry.AlbumArt is { Length: > 0 })
                writer.WriteString("art_b64", Convert.ToBase64String(entry.AlbumArt));
            writer.WriteEndObject();
        }
        catch (Exception ex)
        {
            Log($"write {key}: {ex.Message}");
        }
    }

    /// <summary>Negative-cache: record that a lookup yielded nothing.
    /// Avoids re-querying MusicBrainz every time the same track plays.</summary>
    public void PutMiss(string key) => Put(key, new Entry(null, null, null, null, null));

    private static string? GetString(JsonElement r, string name) =>
        r.TryGetProperty(name, out var p) && p.ValueKind == JsonValueKind.String
            ? p.GetString() : null;

    private static byte[]? GetBase64(JsonElement r, string name)
    {
        var s = GetString(r, name);
        if (string.IsNullOrEmpty(s)) return null;
        try { return Convert.FromBase64String(s); }
        catch { return null; }
    }
}
