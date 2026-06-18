using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Linq;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using HorizonRadio.Core.Diagnostics;

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

    private readonly IMetadataCache _cache;
    private readonly HttpClient _http;
    private readonly bool _ownsHttp;
    private readonly IReadOnlyList<string> _storefronts;

    // Light spacing between calls — the Search API tolerates bursts poorly, and a single
    // lookup can fan out to a few requests across storefronts/query forms.
    private readonly RateGate _rate = new(TimeSpan.FromMilliseconds(250));

    public ItunesProvider(IMetadataCache cache, HttpClient? http = null, string? country = null)
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

        var capture = MetadataTrace.NewCapture();
        var match = await FindBestAsync(title, artist, query.Title, query.Artist, capture, ct).ConfigureAwait(false);
        MetadataTrace.ProviderSearch(Id, string.IsNullOrEmpty(artist) ? title : $"{artist} {title}", capture);
        if (match is null)
        {
            _cache.PutMiss(cacheKey);
            return null;
        }

        var art = match.ArtworkUrl != null
            ? await ImageDownload.TryGetAsync(_http, match.ArtworkUrl, ct).ConfigureAwait(false)
            : null;

        var entry = new MetadataCacheEntry(match.Title, match.Artist, match.Album, art, Mbid: null, Year: match.Year);
        _cache.Put(cacheKey, entry);
        return ToContribution(entry);
    }

    private static MetadataContribution? ToContribution(MetadataCacheEntry e)
    {
        var c = new MetadataContribution(e.Title, e.Artist, e.Album, e.AlbumArt, e.Year);
        return c.IsEmpty ? null : c;
    }

    private async Task<Match?> FindBestAsync(
        string cleanTitle, string cleanArtist, string rawTitle, string? rawArtist,
        ICollection<MetadataTrace.CatalogCandidate>? sink, CancellationToken ct)
    {
        // Full query first (more precise), then title-only (catches artist-credit mismatches).
        var terms = string.IsNullOrEmpty(cleanArtist)
            ? new[] { cleanTitle }
            : new[] { $"{cleanArtist} {cleanTitle}", cleanTitle };

        foreach (var store in _storefronts)
        {
            foreach (var term in terms)
            {
                var match = await SearchOnceAsync(term, store, rawTitle, rawArtist, sink, ct).ConfigureAwait(false);
                if (match != null) return match; // first confident hit; stores are tried in priority order
            }
        }
        return null;
    }

    private async Task<Match?> SearchOnceAsync(string term, string store, string rawTitle, string? rawArtist,
        ICollection<MetadataTrace.CatalogCandidate>? sink, CancellationToken ct)
    {
        // limit=10 so scoring can see past a fuzzy #1 to the real track.
        var url = $"https://itunes.apple.com/search?media=music&entity=song&limit=10&country={store}&term={Uri.EscapeDataString(term)}";

        await _rate.WaitAsync(ct).ConfigureAwait(false);
        using var resp = await _http.GetAsync(url, ct).ConfigureAwait(false);
        if (!resp.IsSuccessStatusCode)
        {
            Log($"HTTP {(int)resp.StatusCode} ({store}) for '{term}'");
            return null;
        }

        using var stream = await resp.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        using var doc = await JsonDocument.ParseAsync(stream, default, ct).ConfigureAwait(false);
        return SelectMatch(doc.RootElement, rawTitle, rawArtist, sink);
    }

    /// <summary>Pick the highest-scoring result that clears the match guard (title-first;
    /// artist a bonus), and lift its fields + a higher-res artwork URL. Pure JSON-in so the
    /// parse/scoring is unit-testable without HTTP. <paramref name="sink"/>, when supplied,
    /// collects every scored result (for the diagnostics trace) without affecting the choice.</summary>
    internal static Match? SelectMatch(JsonElement root, string queryTitle, string? queryArtist,
        ICollection<MetadataTrace.CatalogCandidate>? sink = null)
    {
        if (!root.TryGetProperty("results", out var results) ||
            results.ValueKind != JsonValueKind.Array) return null;

        // For a title-only query, reject a widely-covered/ambiguous title rather than attach a
        // random cover's art; inert when an artist is present.
        var titleGuard = new TitleOnlyGuard(queryArtist);
        Match? best = null;
        double bestScore = double.NegativeInfinity;
        foreach (var r in results.EnumerateArray())
        {
            var title = Str(r, "trackName");
            if (title is null) continue;
            var artist = Str(r, "artistName");

            var score = SearchTerms.MatchScore(queryTitle, queryArtist, title, artist);
            if (score is { }) titleGuard.Observe(artist);
            sink?.Add(new(title, artist, Str(r, "collectionName"), score));
            if (score is not { } sc || sc <= bestScore) continue;

            bestScore = sc;
            best = new Match(
                Title: title,
                Artist: artist,
                Album: Str(r, "collectionName"),
                Year: ParseYear(Str(r, "releaseDate")),
                ArtworkUrl: UpscaleArtwork(Str(r, "artworkUrl100")));
        }
        return titleGuard.IsAmbiguous ? null : best;
    }

    // iTunes returns a 100×100 thumbnail URL; swapping the size segment yields full-res art.
    private static string? UpscaleArtwork(string? url) =>
        string.IsNullOrEmpty(url) ? null : url.Replace("100x100bb", "600x600bb");

    private static int? ParseYear(string? iso) =>
        DateTimeOffset.TryParse(iso, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var dt)
            ? dt.Year : null;

    private static string? Str(JsonElement e, string name) =>
        e.TryGetProperty(name, out var p) && p.ValueKind == JsonValueKind.String ? p.GetString() : null;

    public ValueTask DisposeAsync()
    {
        _rate.Dispose();
        if (_ownsHttp) _http.Dispose();
        return ValueTask.CompletedTask;
    }

    internal sealed record Match(string Title, string? Artist, string? Album, int? Year, string? ArtworkUrl);
}
