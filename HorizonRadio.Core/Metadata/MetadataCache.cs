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
///
/// An entry that <em>has</em> art is kept forever. An entry <em>without</em> art — a miss, or a
/// partial hit (text but no cover) — is only kept until it goes stale: written under an older
/// <see cref="CurrentCacheVersion"/> (so matching/parsing improvements get a fresh chance on a
/// previously-missed song) or older than the retry TTL (catalogs gain art over time). A stale
/// art-less entry is treated as absent so the lookup re-runs. Without this, one miss would be
/// permanent and no future fix could ever surface on a song already seen.
/// </summary>
public sealed class MetadataCache
{
    /// <summary>Bump when matching/parsing logic changes enough that previously art-less results
    /// (misses and partial hits) deserve a retry. Entries stamped with a different version are
    /// treated as stale. Legacy entries (no stamp) read as version 0, so a bump invalidates them.</summary>
    public const int CurrentCacheVersion = 1;

    private static readonly TimeSpan DefaultRetryTtl = TimeSpan.FromDays(14);

    private readonly string _root;
    private readonly TimeSpan _retryTtl;
    private readonly int _cacheVersion;
    private readonly Func<DateTimeOffset> _now;
    private readonly ConcurrentDictionary<string, Entry?> _memoryCache = new();

    public sealed record Entry(
        string? Title,
        string? Artist,
        string? Album,
        byte[]? AlbumArt,
        string? Mbid,
        int? Year = null,
        IReadOnlyList<PlayableRef>? Pvs = null);

    /// <param name="retryTtl">How long an art-less entry (miss / partial hit) is trusted before it
    /// is retried. Defaults to 14 days.</param>
    /// <param name="cacheVersion">Logic version stamped on writes; reads from another version are
    /// stale. Defaults to <see cref="CurrentCacheVersion"/>.</param>
    /// <param name="now">Clock seam for tests. Defaults to <see cref="DateTimeOffset.UtcNow"/>.</param>
    public MetadataCache(string? root = null, TimeSpan? retryTtl = null, int? cacheVersion = null,
                         Func<DateTimeOffset>? now = null)
    {
        _root = root ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "HorizonRadio", "metadata");
        _retryTtl = retryTtl ?? DefaultRetryTtl;
        _cacheVersion = cacheVersion ?? CurrentCacheVersion;
        _now = now ?? (() => DateTimeOffset.UtcNow);
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
            Entry entry;
            bool fresh;
            using (var stream = File.OpenRead(path))
            using (var doc = JsonDocument.Parse(stream))
            {
                var r = doc.RootElement;
                entry = new Entry(
                    Title: GetString(r, "title"),
                    Artist: GetString(r, "artist"),
                    Album: GetString(r, "album"),
                    AlbumArt: GetBase64(r, "art_b64"),
                    Mbid: GetString(r, "mbid"),
                    Year: GetInt(r, "year"),
                    Pvs: GetPvs(r));
                fresh = GetInt(r, "cache_ver") == _cacheVersion
                        && GetLong(r, "cached_at") is { } at
                        && _now() - DateTimeOffset.FromUnixTimeSeconds(at) < _retryTtl;
            }

            // Art never changes for a recording, so an art-bearing entry is kept forever. An
            // art-less one (miss / partial hit) is honored only while fresh; once stale it's
            // dropped and treated as absent so the lookup re-runs and can pick up a fix.
            if (entry.AlbumArt is { Length: > 0 } || fresh)
            {
                _memoryCache[key] = entry;
                return entry;
            }

            TryDelete(path);
            _memoryCache[key] = null;
            return null;
        }
        catch (Exception ex)
        {
            Log($"read {key}: {ex.Message}");
            return null;
        }
    }

    private static void TryDelete(string path)
    {
        try { File.Delete(path); } catch { /* best-effort; a re-search will overwrite it anyway */ }
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
            if (entry.Year is { } year) writer.WriteNumber("year", year);
            if (entry.AlbumArt is { Length: > 0 })
                writer.WriteString("art_b64", Convert.ToBase64String(entry.AlbumArt));
            if (entry.Pvs is { Count: > 0 })
            {
                writer.WriteStartArray("pvs");
                foreach (var pv in entry.Pvs)
                {
                    writer.WriteStartObject();
                    writer.WriteString("service", pv.Service);
                    writer.WriteString("url", pv.Url);
                    writer.WriteEndObject();
                }
                writer.WriteEndArray();
            }
            // Stamp every write so an art-less entry can be aged out (TTL) or invalidated (version).
            writer.WriteNumber("cached_at", _now().ToUnixTimeSeconds());
            writer.WriteNumber("cache_ver", _cacheVersion);
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

    private static int? GetInt(JsonElement r, string name) =>
        r.TryGetProperty(name, out var p) && p.ValueKind == JsonValueKind.Number && p.TryGetInt32(out var i)
            ? i : null;

    private static long? GetLong(JsonElement r, string name) =>
        r.TryGetProperty(name, out var p) && p.ValueKind == JsonValueKind.Number && p.TryGetInt64(out var l)
            ? l : null;

    private static byte[]? GetBase64(JsonElement r, string name)
    {
        var s = GetString(r, name);
        if (string.IsNullOrEmpty(s)) return null;
        try { return Convert.FromBase64String(s); }
        catch { return null; }
    }

    private static List<PlayableRef>? GetPvs(JsonElement r)
    {
        if (!r.TryGetProperty("pvs", out var arr) || arr.ValueKind != JsonValueKind.Array) return null;
        var list = new List<PlayableRef>();
        foreach (var p in arr.EnumerateArray())
        {
            var service = GetString(p, "service");
            var url = GetString(p, "url");
            if (!string.IsNullOrEmpty(service) && !string.IsNullOrEmpty(url))
                list.Add(new PlayableRef(service!, url!));
        }
        return list.Count > 0 ? list : null;
    }
}
