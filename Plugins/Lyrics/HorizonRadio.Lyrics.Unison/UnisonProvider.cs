using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json.Serialization;
using HorizonRadio.Sdk;
using HorizonRadio.Sdk.Lyrics;
using HorizonRadio.Sdk.Metadata;

namespace HorizonRadio.Lyrics.Unison;

/// <summary>
/// Unison (unison.boidu.dev): Better Lyrics' crowdsourced, community-voted lyrics, often hand-synced
/// word by word and keyed to the exact YouTube Music video they were timed against. Much smaller
/// than LRCLIB, so it's asked first and LRCLIB covers the rest.
/// </summary>
public sealed class UnisonProvider : ILyricsProvider
{
    public string Id => "unison";
    public int SortOrder => 10;

    private readonly HttpClient _http;
    private readonly bool _ownsHttp;
    private readonly string _baseUrl;

    public UnisonProvider(HttpClient? http = null, string baseUrl = "https://unison.boidu.dev")
    {
        _ownsHttp = http is null;
        _http = http ?? new HttpClient { Timeout = TimeSpan.FromSeconds(10) };
        _baseUrl = baseUrl.TrimEnd('/');
        var ua = $"HorizonRadio/{BuildInfo.Current.Version} (https://github.com/dhkatz/HorizonRadio)";
        if (!_http.DefaultRequestHeaders.UserAgent.TryParseAdd(ua))
            _http.DefaultRequestHeaders.TryAddWithoutValidation("User-Agent", "HorizonRadio");
    }

    public async Task<SongLyrics?> FindAsync(LyricsQuery query, CancellationToken ct)
    {
        // Unison tries the video id, then song + artist (+ duration ±2 s).
        var args = new List<string>
        {
            $"song={Uri.EscapeDataString(LyricsSearch.Title(query.Title))}",
            $"artist={Uri.EscapeDataString(LyricsSearch.Artist(query.Artist))}",
        };
        if (query.Duration is { } d)
            args.Add($"duration={Math.Round(d.TotalSeconds).ToString(CultureInfo.InvariantCulture)}");
        if (query.YouTubeVideoId is { } v) args.Add($"v={Uri.EscapeDataString(v)}");

        var url = $"{_baseUrl}/lyrics?{string.Join('&', args)}";
        using var resp = await _http.GetAsync(url, ct).ConfigureAwait(false);
        if (resp.StatusCode == HttpStatusCode.NotFound)
        {
            Trace.WriteLine($"[hzn-unison] not found: {url}");
            return null; // "not found" is an answer
        }
        resp.EnsureSuccessStatusCode();
        var body = await resp.Content.ReadFromJsonAsync<Response>(ct).ConfigureAwait(false);
        return body?.Data is { } entry ? ToLyrics(entry, query) : null;
    }

    internal static SongLyrics? ToLyrics(Entry entry, LyricsQuery query)
    {
        // The community hid it or voted it below zero: worse than LRCLIB's.
        if (entry.Hidden || entry.EffectiveScore < 0 || string.IsNullOrWhiteSpace(entry.Lyrics)) return null;

        // Unison matches names loosely. Lyrics keyed to the very video playing are this song;
        // otherwise the entry has to be the song we asked for.
        bool sameVideo = query.YouTubeVideoId != null && entry.VideoId == query.YouTubeVideoId;
        if (!sameVideo && SearchTerms.MatchScore(query.Title, query.Artist, entry.Song, entry.Artist) == null) return null;

        (bool Synced, IReadOnlyList<LyricLine> Lines)? parsed = entry.Format switch
        {
            "ttml" => TtmlParser.Parse(entry.Lyrics),
            "lrc" => LrcParser.Parse(entry.Lyrics),
            _ => (false, LrcParser.Parse(entry.Lyrics).Lines),
        };
        if (parsed is not { } p || p.Lines.Count == 0) return null;

        var lines = p.Lines;
        if (!p.Synced) lines = [.. lines.Select(l => l with { Time = TimeSpan.Zero, Words = null })];
        // Timed to a longer cut than the one playing.
        if (p.Synced && query.Duration is { } d && lines[^1].Time > d) return null;

        var by = entry.Submitter?.DisplayName;
        return new SongLyrics("unison", p.Synced, lines,
            string.IsNullOrWhiteSpace(by) ? "Lyrics from Unison" : $"Lyrics from Unison · synced by {by}");
    }

    private sealed record Response([property: JsonPropertyName("data")] Entry? Data);

    internal sealed record Entry(
        [property: JsonPropertyName("song")] string? Song,
        [property: JsonPropertyName("artist")] string? Artist,
        [property: JsonPropertyName("videoId")] string? VideoId,
        [property: JsonPropertyName("lyrics")] string? Lyrics,
        [property: JsonPropertyName("format")] string? Format,
        [property: JsonPropertyName("effectiveScore")] double EffectiveScore = 0,
        [property: JsonPropertyName("hidden")] bool Hidden = false,
        [property: JsonPropertyName("submitter")] Submitter? Submitter = null);

    internal sealed record Submitter([property: JsonPropertyName("displayName")] string? DisplayName);

    public ValueTask DisposeAsync()
    {
        if (_ownsHttp) _http.Dispose();
        return ValueTask.CompletedTask;
    }
}
