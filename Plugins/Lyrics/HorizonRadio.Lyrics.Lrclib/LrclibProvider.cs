using System.Diagnostics;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json.Serialization;
using HorizonRadio.Sdk;
using HorizonRadio.Sdk.Lyrics;
using HorizonRadio.Sdk.Metadata;

namespace HorizonRadio.Lyrics.Lrclib;

/// <summary>
/// LRCLIB (lrclib.net): free, open, community-synced LRC lyrics. Synced lyrics are matched strictly
/// on the length of the audio that's playing: a file timed to the radio edit is worse than none on
/// the album cut. When nothing synced fits, the unsynced text is used. When the length isn't known
/// (live radio), the song is matched on its title and artist and only the text is shown.
/// </summary>
public sealed class LrclibProvider : ILyricsProvider
{
    public string Id => "lrclib";
    public int SortOrder => 20;

    private const string Credit = "Lyrics from LRCLIB";

    /// <summary>LRCLIB's own <c>/api/get</c> window. Wider lets a differently cut upload borrow the
    /// wrong timing.</summary>
    internal static readonly TimeSpan DurationTolerance = TimeSpan.FromSeconds(2);

    /// <summary>Synced lyrics must run until close to the end of the song. A file that stops halfway
    /// has squeezed the whole song into half of it, so every line arrives early, and more so as the
    /// song goes on. Real files end within an outro of the end (~95% is typical).</summary>
    private const double MinCoverage = 0.6;

    /// <summary>The same lyric file attached to records this far apart in length isn't timed to all
    /// of them: it was pasted.</summary>
    private static readonly TimeSpan CopySpread = TimeSpan.FromSeconds(10);

    private readonly HttpClient _http;
    private readonly bool _ownsHttp;
    private readonly string _baseUrl;

    public LrclibProvider(HttpClient? http = null, string baseUrl = "https://lrclib.net")
    {
        _ownsHttp = http is null;
        _http = http ?? new HttpClient { Timeout = TimeSpan.FromSeconds(10) };
        _baseUrl = baseUrl.TrimEnd('/');
        // LRCLIB asks clients to say who they are.
        var ua = $"HorizonRadio/{BuildInfo.Current.Version} (https://github.com/dhkatz/HorizonRadio)";
        if (!_http.DefaultRequestHeaders.UserAgent.TryParseAdd(ua))
            _http.DefaultRequestHeaders.TryAddWithoutValidation("User-Agent", "HorizonRadio");
    }

    public async Task<SongLyrics?> FindAsync(LyricsQuery query, CancellationToken ct)
    {
        var title = LyricsSearch.Title(query.Title);
        var artist = LyricsSearch.Artist(query.Artist);
        if (title.Length == 0) return null;

        var records = await SearchAsync($"track_name={Uri.EscapeDataString(title)}&artist_name={Uri.EscapeDataString(artist)}", ct)
            .ConfigureAwait(false);
        var found = Pick(records, query);
        if (found != null) return found;

        // The field search is strict about how the artist is written; the free-text search isn't.
        records = await SearchAsync($"q={Uri.EscapeDataString($"{artist} {title}")}", ct).ConfigureAwait(false);
        return Pick(records, query);
    }

    private async Task<IReadOnlyList<Record>> SearchAsync(string queryString, CancellationToken ct)
    {
        var url = $"{_baseUrl}/api/search?{queryString}";
        var resp = await _http.GetAsync(url, ct).ConfigureAwait(false);
        if (resp.StatusCode == HttpStatusCode.TooManyRequests)
        {
            // Rate limited (a burst of skips): wait as asked, briefly, and try once more.
            var wait = resp.Headers.RetryAfter?.Delta ?? TimeSpan.FromSeconds(1);
            resp.Dispose();
            await Task.Delay(wait < MaxRetryWait ? wait : MaxRetryWait, ct).ConfigureAwait(false);
            resp = await _http.GetAsync(url, ct).ConfigureAwait(false);
        }
        using (resp)
        {
            resp.EnsureSuccessStatusCode();
            var records = await resp.Content.ReadFromJsonAsync<List<Record>>(ct).ConfigureAwait(false);
            Trace.WriteLine($"[hzn-lrclib] {records?.Count ?? 0} records: {url}");
            return records ?? [];
        }
    }

    private static readonly TimeSpan MaxRetryWait = TimeSpan.FromSeconds(3);

    internal static SongLyrics? Pick(IReadOnlyList<Record> records, LyricsQuery query) =>
        query.Duration is { } duration ? PickTimed(records, duration) : PickByName(records, query);

    // The length of the audio is known: only lyrics timed to a recording of that length.
    private static SongLyrics? PickTimed(IReadOnlyList<Record> records, TimeSpan duration)
    {
        var copied = CopiedAcrossLengths(records);
        var close = records
            .Select(r => (Record: r, Diff: (r.Length - duration).Duration()))
            .Where(x => x.Diff <= DurationTolerance)
            .OrderBy(x => x.Diff)
            .Select(x => x.Record)
            .ToList();

        // LRCLIB is community-submitted: a record's stated length can match while its timestamps
        // are junk. Judge the lyrics themselves, not only the label.
        var usable = new List<(SongLyrics Lyrics, bool Copied)>();
        foreach (var r in close)
        {
            var text = r.SyncedLyrics?.Trim();
            if (string.IsNullOrEmpty(text)) continue;
            var (synced, lines) = LrcParser.Parse(text);
            if (!synced || lines.Count == 0) continue;
            var last = lines[^1].Time;
            if (last < duration * MinCoverage || last > duration) continue;
            usable.Add((new SongLyrics("lrclib", true, lines, Credit), copied.Contains(text)));
        }
        // A file pasted onto every compilation and remaster is normal for a popular song, and is
        // usually right for the standard cut, but a record timed on its own is the safer bet.
        var best = usable.FirstOrDefault(u => !u.Copied).Lyrics ?? usable.FirstOrDefault().Lyrics;
        if (best != null) return best;

        // Unsynced text beats synced lyrics that run ahead of the singer.
        var plain = close.FirstOrDefault(r => !string.IsNullOrWhiteSpace(r.PlainLyrics));
        return plain == null ? null : Unsynced(plain.PlainLyrics!);
    }

    // The length isn't known (live radio): match the song by name. Radio follows the song from
    // where its title changed, so the timing matters, but with no length to check it against,
    // take the cut most uploads agree on (the standard one, not a radio edit or an extended mix).
    private static SongLyrics? PickByName(IReadOnlyList<Record> records, LyricsQuery query)
    {
        var matching = records
            .Where(r => !r.Instrumental
                        && SearchTerms.MatchScore(query.Title, query.Artist, r.TrackName, r.ArtistName) != null)
            .ToList();
        var timed = matching
            .Where(r => !string.IsNullOrWhiteSpace(r.SyncedLyrics))
            .GroupBy(r => (int)Math.Round(r.Duration / DurationTolerance.TotalSeconds))
            .OrderByDescending(g => g.Count());
        foreach (var cut in timed)
        {
            foreach (var r in cut)
            {
                var (synced, lines) = LrcParser.Parse(r.SyncedLyrics!.Trim());
                if (synced && lines.Count > 0 && lines[^1].Time <= r.Length)
                    return new SongLyrics("lrclib", true, lines, Credit);
            }
        }
        var plain = matching.FirstOrDefault(r => !string.IsNullOrWhiteSpace(r.PlainLyrics));
        return plain == null ? null : Unsynced(plain.PlainLyrics!);
    }

    private static SongLyrics Unsynced(string text) =>
        new("lrclib", false, [.. LrcParser.Parse(text).Lines.Select(l => l with { Time = TimeSpan.Zero, Words = null })], Credit);

    // Synced lyric texts that appear on records of very different lengths: pasted, not timed.
    private static HashSet<string> CopiedAcrossLengths(IReadOnlyList<Record> records)
    {
        var spans = new Dictionary<string, (TimeSpan Min, TimeSpan Max)>(StringComparer.Ordinal);
        foreach (var r in records)
        {
            var text = r.SyncedLyrics?.Trim();
            if (string.IsNullOrEmpty(text)) continue;
            spans[text] = spans.TryGetValue(text, out var s)
                ? (r.Length < s.Min ? r.Length : s.Min, r.Length > s.Max ? r.Length : s.Max)
                : (r.Length, r.Length);
        }
        return [.. spans.Where(kv => kv.Value.Max - kv.Value.Min > CopySpread).Select(kv => kv.Key)];
    }

    internal sealed record Record(
        [property: JsonPropertyName("trackName")] string? TrackName,
        [property: JsonPropertyName("artistName")] string? ArtistName,
        [property: JsonPropertyName("duration")] double Duration,
        [property: JsonPropertyName("instrumental")] bool Instrumental,
        [property: JsonPropertyName("plainLyrics")] string? PlainLyrics,
        [property: JsonPropertyName("syncedLyrics")] string? SyncedLyrics)
    {
        public TimeSpan Length => TimeSpan.FromSeconds(Duration);
    }

    public ValueTask DisposeAsync()
    {
        if (_ownsHttp) _http.Dispose();
        return ValueTask.CompletedTask;
    }
}
