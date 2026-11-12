using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using HorizonRadio.Core.IO;
using HorizonRadio.Sdk.Sources;

namespace HorizonRadio.Core.Sources.Mixes;

/// <summary>
/// Keeps what each mix entry was found to be (<see cref="MixEntryInfo"/>), so the Mixes tab shows
/// titles and covers at once instead of looking every entry up again each session. Stored under
/// <c>%LOCALAPPDATA%\HorizonRadio\mix-entries\</c>: <c>index.json</c> holds the text, keyed by
/// source and locator, and each cover is its own file beside it. Entries no mix holds any more
/// are dropped by <see cref="Prune"/>.
/// </summary>
public sealed class MixEntryInfoStore
{
    /// <summary>A kept description and when it was looked up.</summary>
    public sealed record Kept(MixEntryInfo Info, DateTimeOffset Updated);

    private sealed record Row(string Source, string Locator, MixEntryInfo Info, string? ArtFile, DateTimeOffset Updated);

    private readonly string _dir;
    private readonly Lock _lock = new();
    private readonly Dictionary<string, Row> _rows = new(StringComparer.Ordinal);
    private bool _loaded;

    /// <param name="dir">Where to keep the index and covers; defaults to
    /// <c>%LOCALAPPDATA%\HorizonRadio\mix-entries</c>.</param>
    public MixEntryInfoStore(string? dir = null)
    {
        _dir = dir ?? AppPaths.In("mix-entries");
    }

    private string IndexPath => Path.Combine(_dir, "index.json");

    private static string Key(string source, string locator) => $"{source}\n{locator}";

    public Kept? Get(ContentRef entry)
    {
        lock (_lock)
        {
            EnsureLoaded();
            var key = Key(entry.SourceId, entry.Locator);
            if (!_rows.TryGetValue(key, out var row)) return null;
            if (row.Info.Art is null && row.ArtFile is { } file)
            {
                // Covers are read on first use, not with the index.
                try
                {
                    row = row with { Info = row.Info with { Art = File.ReadAllBytes(Path.Combine(_dir, file)) } };
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    row = row with { ArtFile = null };
                }
                _rows[key] = row;
            }
            return new Kept(row.Info, row.Updated);
        }
    }

    public void Put(ContentRef entry, MixEntryInfo info)
    {
        lock (_lock)
        {
            EnsureLoaded();
            var artFile = ArtFileName(entry);
            if (info.Art is { Length: > 0 } art)
            {
                try { AtomicFile.WriteAllBytes(Path.Combine(_dir, artFile), art); }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    Log($"cover write failed: {ex.Message}");
                    artFile = null;
                }
            }
            else
            {
                DeleteArt(artFile);
                artFile = null;
            }
            _rows[Key(entry.SourceId, entry.Locator)] =
                new Row(entry.SourceId, entry.Locator, info, artFile, DateTimeOffset.UtcNow);
            Save();
        }
    }

    /// <summary>Forget every entry not in <paramref name="keep"/>, covers included.</summary>
    public void Prune(IEnumerable<ContentRef> keep)
    {
        var wanted = keep.Select(e => Key(e.SourceId, e.Locator)).ToHashSet(StringComparer.Ordinal);
        lock (_lock)
        {
            EnsureLoaded();
            var gone = _rows.Where(kv => !wanted.Contains(kv.Key)).ToList();
            if (gone.Count == 0) return;
            foreach (var (key, row) in gone)
            {
                _rows.Remove(key);
                if (row.ArtFile is { } file) DeleteArt(file);
            }
            Save();
        }
    }

    // One cover file per entry, named for it so a re-describe overwrites rather than accumulates.
    private static string ArtFileName(ContentRef entry)
    {
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(Key(entry.SourceId, entry.Locator)));
        return Convert.ToHexStringLower(hash.AsSpan(0, 12)) + ".img";
    }

    private void DeleteArt(string file)
    {
        try { File.Delete(Path.Combine(_dir, file)); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { /* best effort */ }
    }

    private void EnsureLoaded()
    {
        if (_loaded) return;
        _loaded = true;
        try
        {
            if (!File.Exists(IndexPath)) return;
            using var stream = File.OpenRead(IndexPath);
            using var doc = JsonDocument.Parse(stream);
            if (!doc.RootElement.TryGetProperty("entries", out var arr) || arr.ValueKind != JsonValueKind.Array) return;
            foreach (var e in arr.EnumerateArray())
            {
                if (e.ValueKind != JsonValueKind.Object) continue;
                var source = Str(e, "source");
                var locator = Str(e, "locator");
                var title = Str(e, "title");
                if (source is null || locator is null || title is null) continue;
                var kind = Enum.TryParse<ContentKind>(Str(e, "kind"), ignoreCase: true, out var k) ? k : ContentKind.Track;
                int? count = e.TryGetProperty("count", out var c) && c.ValueKind == JsonValueKind.Number &&
                             c.TryGetInt32(out var n) ? n : null;
                var updated = e.TryGetProperty("updated", out var u) && u.ValueKind == JsonValueKind.String &&
                              u.TryGetDateTimeOffset(out var t) ? t : DateTimeOffset.MinValue;
                _rows[Key(source, locator)] = new Row(source, locator,
                    new MixEntryInfo(title, Str(e, "subtitle"), kind, count, null), Str(e, "art"), updated);
            }
        }
        catch (Exception ex)
        {
            Log($"load failed (starting empty): {ex.Message}");
            _rows.Clear();
        }
    }

    private void Save()
    {
        try
        {
            AtomicFile.Write(IndexPath, stream =>
            {
                using var w = new Utf8JsonWriter(stream, new JsonWriterOptions { Indented = true });
                w.WriteStartObject();
                w.WriteStartArray("entries");
                foreach (var row in _rows.Values)
                {
                    w.WriteStartObject();
                    w.WriteString("source", row.Source);
                    w.WriteString("locator", row.Locator);
                    w.WriteString("title", row.Info.Title);
                    if (row.Info.Subtitle is { } sub) w.WriteString("subtitle", sub);
                    w.WriteString("kind", row.Info.Kind.ToString().ToLowerInvariant());
                    if (row.Info.ItemCount is { } count) w.WriteNumber("count", count);
                    if (row.ArtFile is { } art) w.WriteString("art", art);
                    w.WriteString("updated", row.Updated);
                    w.WriteEndObject();
                }
                w.WriteEndArray();
                w.WriteEndObject();
            });
        }
        catch (Exception ex)
        {
            Log($"save failed: {ex.Message}");
        }
    }

    private static string? Str(JsonElement e, string key) =>
        e.TryGetProperty(key, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    private static void Log(string msg) => Trace.WriteLine($"[hzn-mix-entries] {msg}");
}
