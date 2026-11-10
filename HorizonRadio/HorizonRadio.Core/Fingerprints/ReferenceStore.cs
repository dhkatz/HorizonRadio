using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using HorizonRadio.Core.IO;

namespace HorizonRadio.Core.Fingerprints;

/// <summary>A recording of a song, fingerprinted.</summary>
/// <param name="Locator">The page it was fetched from.</param>
/// <param name="Duration">Its length.</param>
/// <param name="LeadIn">The silence before its first sound (<see cref="Silence"/>).</param>
/// <param name="TrailOut">The silence after its last sound.</param>
/// <param name="Fingerprint">Its Chromaprint fingerprint (<see cref="Chromaprint"/>).</param>
/// <param name="MatchRate">How closely a station's audio lined up with it (the share of fingerprint
/// bits that differed), or null when none has: the closest is tried first next time.</param>
/// <param name="StationStart">Where a station's version of the song starts in it, when the recording
/// opens with a prelude stations don't play (a music video's scene before the song); learned the first
/// time a station is heard starting the song. Null otherwise: the song starts at the first sound.</param>
public sealed record StoredReference(
    string Locator,
    TimeSpan Duration,
    TimeSpan LeadIn,
    TimeSpan TrailOut,
    uint[] Fingerprint,
    double? MatchRate,
    TimeSpan? StationStart = null)
{
    /// <summary>Where the song starts in the recording: where a station starts it, else at the
    /// recording's first sound. The song's time counts from here.</summary>
    public TimeSpan SongStart => StationStart ?? LeadIn;

    /// <summary>How long the song lasts in the recording, from <see cref="SongStart"/> to its last sound.</summary>
    public TimeSpan SongLength => Duration - SongStart - TrailOut is var length && length > TimeSpan.Zero ? length : TimeSpan.Zero;
}

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
        _directory = directory ?? AppPaths.In("fingerprints");

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
                Trace.WriteLine($"[hzn-sync] couldn't store a fingerprint: {ex.Message}");
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
            Trace.WriteLine($"[hzn-sync] couldn't read stored fingerprints: {ex.Message}");
            return [];
        }
    }

    private string PathFor(string songKey) =>
        Path.Combine(_directory, Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(songKey)))[..24] + ".json");

    private sealed record SongFile(string Song, ReferenceFile[] References);

    private sealed record ReferenceFile(
        string Locator, double Seconds, double LeadIn, double TrailOut, string Fingerprint, double? MatchRate,
        double? StationStart)
    {
        public static ReferenceFile From(StoredReference r)
        {
            var bytes = new byte[r.Fingerprint.Length * sizeof(uint)];
            Buffer.BlockCopy(r.Fingerprint, 0, bytes, 0, bytes.Length);
            return new ReferenceFile(r.Locator, r.Duration.TotalSeconds, r.LeadIn.TotalSeconds, r.TrailOut.TotalSeconds,
                Convert.ToBase64String(bytes), r.MatchRate, r.StationStart?.TotalSeconds);
        }

        public StoredReference? ToReference()
        {
            try
            {
                var bytes = Convert.FromBase64String(Fingerprint);
                var words = new uint[bytes.Length / sizeof(uint)];
                Buffer.BlockCopy(bytes, 0, words, 0, words.Length * sizeof(uint));
                return new StoredReference(Locator, TimeSpan.FromSeconds(Seconds), TimeSpan.FromSeconds(LeadIn),
                    TimeSpan.FromSeconds(TrailOut), words, MatchRate,
                    StationStart is { } s ? TimeSpan.FromSeconds(s) : null);
            }
            catch (FormatException)
            {
                return null;
            }
        }
    }
}
