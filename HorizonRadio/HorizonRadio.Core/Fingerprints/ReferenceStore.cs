using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using HorizonRadio.Core.IO;

namespace HorizonRadio.Core.Fingerprints;

/// <summary>A recording of a song, fingerprinted.</summary>
/// <param name="Locator">The page it was fetched from.</param>
/// <param name="YouTubeVideoId">Its YouTube video id, when it's a YouTube video.</param>
/// <param name="Duration">Its length.</param>
/// <param name="Fingerprint">Its Chromaprint fingerprint (<see cref="Chromaprint"/>).</param>
/// <param name="MatchRate">How closely a station's audio lined up with it (the share of fingerprint
/// bits that differed), or null when none has: the closest is tried first next time.</param>
public sealed record StoredReference(
    string Locator,
    string? YouTubeVideoId,
    TimeSpan Duration,
    uint[] Fingerprint,
    double? MatchRate);

/// <summary>
/// The fingerprints of songs' reference recordings, kept on disk so each recording is downloaded once:
/// the next time a station plays the song, its audio is lined up against the stored fingerprint. One
/// small file per song (a four-minute fingerprint is about 8 KB) under
/// <c>%LOCALAPPDATA%\HorizonRadio\fingerprints</c>.
/// </summary>
public sealed class ReferenceStore
{
    private static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    private readonly string _directory;
    private readonly Lock _lock = new();

    public ReferenceStore(string? directory = null) =>
        _directory = directory ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "HorizonRadio", "fingerprints");

    /// <summary>What a song is stored under: its artist and title, the way they're written aside.</summary>
    public static string SongKey(string artist, string title) =>
        $"{artist.Trim().ToUpperInvariant()}\u001f{title.Trim().ToUpperInvariant()}";

    /// <summary>The recordings stored for <paramref name="songKey"/>: the ones that matched, closest
    /// first, then the rest.</summary>
    public IReadOnlyList<StoredReference> For(string songKey)
    {
        lock (_lock)
        {
            return [.. Load(songKey).OrderBy(r => r.MatchRate ?? double.MaxValue)];
        }
    }

    /// <summary>Store <paramref name="reference"/> for <paramref name="songKey"/>, replacing the one
    /// from the same page.</summary>
    public void Save(string songKey, StoredReference reference)
    {
        lock (_lock)
        {
            var all = Load(songKey).Where(r => r.Locator != reference.Locator).Append(reference).ToList();
            var file = new SongFile(songKey, [.. all.Select(ReferenceFile.From)]);
            try
            {
                Directory.CreateDirectory(_directory);
                AtomicFile.WriteAllBytes(PathFor(songKey), JsonSerializer.SerializeToUtf8Bytes(file, Json));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                Debug.WriteLine($"[hzn-sync] couldn't store a fingerprint: {ex.Message}");
            }
        }
    }

    private List<StoredReference> Load(string songKey)
    {
        try
        {
            var path = PathFor(songKey);
            if (!File.Exists(path)) return [];
            var file = JsonSerializer.Deserialize<SongFile>(File.ReadAllText(path), Json);
            // Another song with the same hash prefix isn't this one.
            if (file?.Song != songKey) return [];
            return [.. file.References.Select(r => r.ToReference()).OfType<StoredReference>()];
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            Debug.WriteLine($"[hzn-sync] couldn't read stored fingerprints: {ex.Message}");
            return [];
        }
    }

    private string PathFor(string songKey) =>
        Path.Combine(_directory, Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(songKey)))[..24] + ".json");

    private sealed record SongFile(string Song, ReferenceFile[] References);

    private sealed record ReferenceFile(string Locator, string? YouTubeVideoId, double Seconds, string Fingerprint, double? MatchRate)
    {
        public static ReferenceFile From(StoredReference r)
        {
            var bytes = new byte[r.Fingerprint.Length * sizeof(uint)];
            Buffer.BlockCopy(r.Fingerprint, 0, bytes, 0, bytes.Length);
            return new ReferenceFile(r.Locator, r.YouTubeVideoId, r.Duration.TotalSeconds, Convert.ToBase64String(bytes), r.MatchRate);
        }

        public StoredReference? ToReference()
        {
            try
            {
                var bytes = Convert.FromBase64String(Fingerprint);
                var words = new uint[bytes.Length / sizeof(uint)];
                Buffer.BlockCopy(bytes, 0, words, 0, words.Length * sizeof(uint));
                return new StoredReference(Locator, YouTubeVideoId, TimeSpan.FromSeconds(Seconds), words, MatchRate);
            }
            catch (FormatException)
            {
                return null;
            }
        }
    }
}
