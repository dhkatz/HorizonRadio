using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using HorizonRadio.Core.Diagnostics;
using SpotifyAPI.Web;

namespace HorizonRadio.Core.Metadata.Spotify;

/// <summary>
/// Metadata via the Spotify Web API. The client comes from a factory delegate so this
/// provider works two ways: with its own app credentials (client-credentials flow), or —
/// when none are configured — by riding on the already-connected Spotify <em>source</em>
/// (Sources tab), so the user needn't register a second app. The delegate is read each
/// lookup, so reconnecting the source (or connecting it after startup) just works.
///
/// Text lookups search free-text and score results title-first
/// (<see cref="SearchTerms.MatchScore"/>) rather than gating on an exact artist match —
/// the broadcast artist often differs from Spotify's credit (a producer vs. a vocalist).
/// </summary>
public sealed class SpotifyProvider : IMetadataProvider
{
    public string Id => "spotify";

    private readonly MetadataCache _cache;
    private readonly Func<CancellationToken, Task<SpotifyClient?>> _clientFactory;
    private readonly HttpClient _httpForArt = new() { Timeout = TimeSpan.FromSeconds(15) };

    public SpotifyProvider(MetadataCache cache, Func<CancellationToken, Task<SpotifyClient?>> clientFactory)
    {
        _cache = cache;
        _clientFactory = clientFactory;
    }

    private static void Log(string msg) => Debug.WriteLine($"[hzn-spotify-mp] {msg}");

    public async Task<MetadataContribution?> ContributeAsync(MetadataQuery query, CancellationToken ct)
    {
        string? queryKey =
            !string.IsNullOrEmpty(query.ExternalId) &&
            query.ExternalId.StartsWith("spotify:track:", StringComparison.Ordinal)
                ? "uri=" + query.ExternalId
                : !string.IsNullOrEmpty(query.Title)
                    ? $"text={query.Artist.ToLowerInvariant()}|{query.Title.ToLowerInvariant()}"
                    : null;
        if (queryKey == null) return null;

        var cacheKey = MetadataCache.Key(Id, queryKey);
        var hit = _cache.TryGet(cacheKey);
        if (hit != null) return ToContribution(hit);

        var entry = queryKey.StartsWith("uri=", StringComparison.Ordinal)
            ? await EnrichByUriAsync(query.ExternalId!, ct).ConfigureAwait(false)
            : await EnrichByTextAsync(query.Artist, query.Title, ct).ConfigureAwait(false);

        if (entry == null) { _cache.PutMiss(cacheKey); return null; }
        _cache.Put(cacheKey, entry);
        return ToContribution(entry);
    }

    // Raw findings as a contribution — the resolver decides which fields win.
    private static MetadataContribution? ToContribution(MetadataCache.Entry e)
    {
        var c = new MetadataContribution(e.Title, e.Artist, e.Album, e.AlbumArt, e.Year);
        return c.IsEmpty ? null : c;
    }

    private async Task<MetadataCache.Entry?> EnrichByUriAsync(string spotifyUri, CancellationToken ct)
    {
        try
        {
            var client = await _clientFactory(ct).ConfigureAwait(false);
            if (client is null) return null;
            var id = spotifyUri.Substring("spotify:track:".Length);
            var track = await client.Tracks.Get(id, ct).ConfigureAwait(false);
            return await BuildEntry(track, ct).ConfigureAwait(false);
        }
        catch (APIException ex)
        {
            Log($"Tracks.Get {spotifyUri}: HTTP {(int?)ex.Response?.StatusCode}: {ex.Message}");
            return null;
        }
        catch (Exception ex)
        {
            Log($"Tracks.Get {spotifyUri}: {ex.GetType().Name}: {ex.Message}");
            return null;
        }
    }

    private async Task<MetadataCache.Entry?> EnrichByTextAsync(string artist, string title, CancellationToken ct)
    {
        try
        {
            var client = await _clientFactory(ct).ConfigureAwait(false);
            if (client is null) return null;

            var cleanTitle = SearchTerms.CleanForSearch(title);
            if (string.IsNullOrEmpty(cleanTitle)) return null;
            var cleanArtist = SearchTerms.CleanForSearch(artist);
            var q = string.IsNullOrEmpty(cleanArtist) ? cleanTitle : $"{cleanArtist} {cleanTitle}";

            // Dev-Mode apps cap the search limit at 10; fetch a handful so scoring can see
            // past a fuzzy top hit to the real track.
            var resp = await client.Search.Item(
                new SearchRequest(SearchRequest.Types.Track, q) { Limit = 10 }, ct).ConfigureAwait(false);

            var items = resp.Tracks?.Items;

            // Capture the query + every scored result for the trace (only when capturing) so this
            // lookup can be replayed through SearchTerms.MatchScore offline.
            var capture = MetadataTrace.Enabled ? new List<MetadataTrace.CatalogCandidate>() : null;
            // Title-only query (no artist): collect the artist of every title-match so we can reject
            // an ambiguous, widely-covered title rather than attach a random cover's art.
            var titleOnlyKeys = string.IsNullOrWhiteSpace(artist) ? new HashSet<string>(StringComparer.Ordinal) : null;
            FullTrack? best = null;
            double bestScore = double.NegativeInfinity;
            foreach (var t in items ?? [])
            {
                var resultArtist = t.Artists?.FirstOrDefault()?.Name;
                var score = SearchTerms.MatchScore(title, artist, t.Name, resultArtist);
                if (score is { }) titleOnlyKeys?.Add(SearchTerms.ArtistKey(resultArtist));
                capture?.Add(new(t.Name, resultArtist, t.Album?.Name, score));
                if (score is not { } sc || sc <= bestScore) continue;
                bestScore = sc;
                best = t;
            }
            if (capture is not null) MetadataTrace.ProviderSearch(Id, q, capture);
            if (titleOnlyKeys is { Count: > 1 }) return null;   // ambiguous title-only → don't guess

            return best is null ? null : await BuildEntry(best, ct).ConfigureAwait(false);
        }
        catch (APIException ex)
        {
            Log($"Search {artist}/{title}: HTTP {(int?)ex.Response?.StatusCode}: {ex.Message}");
            return null;
        }
        catch (Exception ex)
        {
            Log($"Search {artist}/{title}: {ex.GetType().Name}: {ex.Message}");
            return null;
        }
    }

    private async Task<MetadataCache.Entry?> BuildEntry(FullTrack t, CancellationToken ct)
    {
        var artistName = t.Artists?.FirstOrDefault()?.Name;
        var albumName = t.Album?.Name;

        // Smallest image ≥ 300 px; matches the 180×180 HUD tile without
        // pulling a megabyte original.
        byte[]? art = null;
        if (t.Album?.Images is { Count: > 0 } imgs)
        {
            var pick = imgs.Where(i => Math.Max(i.Width, i.Height) >= 300)
                           .OrderBy(i => Math.Max(i.Width, i.Height))
                           .FirstOrDefault()
                    ?? imgs[0];
            if (!string.IsNullOrEmpty(pick.Url))
                art = await ImageDownload.TryGetAsync(_httpForArt, pick.Url, ct).ConfigureAwait(false);
        }

        return new MetadataCache.Entry(
            Title: t.Name,
            Artist: artistName,
            Album: albumName,
            AlbumArt: art,
            Mbid: t.Uri,
            Year: ParseYear(t.Album?.ReleaseDate));
    }

    // Spotify release dates are "YYYY", "YYYY-MM", or "YYYY-MM-DD".
    private static int? ParseYear(string? releaseDate)
    {
        if (string.IsNullOrEmpty(releaseDate) || releaseDate.Length < 4) return null;
        return int.TryParse(releaseDate.AsSpan(0, 4), out var y) ? y : null;
    }

    public ValueTask DisposeAsync()
    {
        _httpForArt.Dispose();
        return ValueTask.CompletedTask;
    }
}
