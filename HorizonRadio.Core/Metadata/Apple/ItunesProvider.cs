using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Linq;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace HorizonRadio.Core.Metadata.Apple;

/// <summary>
/// Metadata via Apple's public iTunes Search API — keyless, broad coverage (notably for
/// Japanese / Vocaloid / doujin catalogues that MusicBrainz often lacks), and it returns
/// cover art directly. The same source third-party "now playing" sites use to match radio
/// streams.
///
/// To find as much art as those sites do, a single lookup tries multiple storefronts —
/// the configured one plus Japan and the US, since Vocaloid/doujin releases are usually
/// JP-store-only — and two query forms (artist+title, then title alone, because the
/// broadcast artist often differs from the store credit). Results are scored title-first
/// (<see cref="SearchTerms.MatchScore"/>); the best confident hit wins.
/// </summary>
public sealed class ItunesProvider : IMetadataProvider
{
    public string Id => "itunes";

    private readonly MetadataCache _cache;
    private readonly HttpClient _http;
    private readonly bool _ownsHttp;
    private readonly IReadOnlyList<string> _storefronts;

    // Light spacing between calls — the Search API tolerates bursts poorly, and a single
    // lookup can fan out to a few requests across storefronts/query forms.
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly Stopwatch _sinceLast = Stopwatch.StartNew();

    public ItunesProvider(MetadataCache cache, HttpClient? http = null, string? country = null)
    {
        _cache = cache;
        _ownsHttp = http is null;
        _http = http ?? new HttpClient { Timeout = TimeSpan.FromSeconds(15) };

        // Search the configured store first (default US), then JP and US as fallbacks so a
        // region-locked release is still found. Distinct, order preserved.
        var primary = string.IsNullOrWhiteSpace(country) ? "us" : country.Trim().ToLowerInvariant();
        _storefronts = new[] { primary, "jp", "us" }.Distinct().ToList();
    }

    private static void Log(string msg) => Debug.WriteLine($"[hzn-itunes] {msg}");

    public async Task<MetadataContribution?> ContributeAsync(MetadataQuery query, CancellationToken ct)
    {
        var title = SearchTerms.CleanForSearch(query.Title);
        if (string.IsNullOrEmpty(title)) return null;
        var artist = SearchTerms.CleanForSearch(query.Artist);

        // Cache by the song, not the storefront/term, so the resolved result (or miss) is
        // reused across stores and query forms.
        var cacheKey = MetadataCache.Key(Id, $"{artist.ToLowerInvariant()}|{title.ToLowerInvariant()}");
        var hit = _cache.TryGet(cacheKey);
        if (hit != null) return ToContribution(hit);

        var match = await FindBestAsync(title, artist, query.Title, query.Artist, ct).ConfigureAwait(false);
        if (match is null)
        {
            _cache.PutMiss(cacheKey);
            return null;
        }

        var art = match.ArtworkUrl != null
            ? await TryDownloadAsync(match.ArtworkUrl, ct).ConfigureAwait(false)
            : null;

        var entry = new MetadataCache.Entry(match.Title, match.Artist, match.Album, art, Mbid: null, Year: match.Year);
        _cache.Put(cacheKey, entry);
        return ToContribution(entry);
    }

    private static MetadataContribution? ToContribution(MetadataCache.Entry e)
    {
        var c = new MetadataContribution(e.Title, e.Artist, e.Album, e.AlbumArt, e.Year);
        return c.IsEmpty ? null : c;
    }

    private async Task<Match?> FindBestAsync(
        string cleanTitle, string cleanArtist, string rawTitle, string? rawArtist, CancellationToken ct)
    {
        // Full query first (more precise), then title-only (catches artist-credit mismatches).
        var terms = string.IsNullOrEmpty(cleanArtist)
            ? new[] { cleanTitle }
            : new[] { $"{cleanArtist} {cleanTitle}", cleanTitle };

        foreach (var store in _storefronts)
        {
            foreach (var term in terms)
            {
                var match = await SearchOnceAsync(term, store, rawTitle, rawArtist, ct).ConfigureAwait(false);
                if (match != null) return match; // first confident hit; stores are tried in priority order
            }
        }
        return null;
    }

    private async Task<Match?> SearchOnceAsync(string term, string store, string rawTitle, string? rawArtist, CancellationToken ct)
    {
        // limit=10 so scoring can see past a fuzzy #1 to the real track.
        var url = $"https://itunes.apple.com/search?media=music&entity=song&limit=10&country={store}&term={Uri.EscapeDataString(term)}";

        await ThrottleAsync(ct).ConfigureAwait(false);
        using var resp = await _http.GetAsync(url, ct).ConfigureAwait(false);
        if (!resp.IsSuccessStatusCode)
        {
            Log($"HTTP {(int)resp.StatusCode} ({store}) for '{term}'");
            return null;
        }

        using var stream = await resp.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        using var doc = await JsonDocument.ParseAsync(stream, default, ct).ConfigureAwait(false);
        return SelectMatch(doc.RootElement, rawTitle, rawArtist);
    }

    /// <summary>Pick the highest-scoring result that clears the match guard (title-first;
    /// artist a bonus), and lift its fields + a higher-res artwork URL. Pure JSON-in so the
    /// parse/scoring is unit-testable without HTTP.</summary>
    internal static Match? SelectMatch(JsonElement root, string queryTitle, string? queryArtist)
    {
        if (!root.TryGetProperty("results", out var results) ||
            results.ValueKind != JsonValueKind.Array) return null;

        Match? best = null;
        double bestScore = double.NegativeInfinity;
        foreach (var r in results.EnumerateArray())
        {
            var title = Str(r, "trackName");
            if (title is null) continue;
            var artist = Str(r, "artistName");

            if (SearchTerms.MatchScore(queryTitle, queryArtist, title, artist) is not { } score) continue;
            if (score <= bestScore) continue;

            bestScore = score;
            best = new Match(
                Title: title,
                Artist: artist,
                Album: Str(r, "collectionName"),
                Year: ParseYear(Str(r, "releaseDate")),
                ArtworkUrl: UpscaleArtwork(Str(r, "artworkUrl100")));
        }
        return best;
    }

    private async Task ThrottleAsync(CancellationToken ct)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var min = TimeSpan.FromMilliseconds(250);
            var elapsed = _sinceLast.Elapsed;
            if (elapsed < min) await Task.Delay(min - elapsed, ct).ConfigureAwait(false);
            _sinceLast.Restart();
        }
        finally { _gate.Release(); }
    }

    // iTunes returns a 100×100 thumbnail URL; swapping the size segment yields full-res art.
    private static string? UpscaleArtwork(string? url) =>
        string.IsNullOrEmpty(url) ? null : url.Replace("100x100bb", "600x600bb");

    private static int? ParseYear(string? iso) =>
        DateTimeOffset.TryParse(iso, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var dt)
            ? dt.Year : null;

    private static string? Str(JsonElement e, string name) =>
        e.TryGetProperty(name, out var p) && p.ValueKind == JsonValueKind.String ? p.GetString() : null;

    private async Task<byte[]?> TryDownloadAsync(string url, CancellationToken ct)
    {
        try { return await _http.GetByteArrayAsync(url, ct).ConfigureAwait(false); }
        catch (Exception ex) { Log($"art fetch failed: {ex.Message}"); return null; }
    }

    public ValueTask DisposeAsync()
    {
        _gate.Dispose();
        if (_ownsHttp) _http.Dispose();
        return ValueTask.CompletedTask;
    }

    internal sealed record Match(string Title, string? Artist, string? Album, int? Year, string? ArtworkUrl);
}
