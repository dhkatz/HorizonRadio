using System;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using HorizonRadio.Core.Models;

namespace HorizonRadio.Core.Metadata;

/// <summary>
/// Enriches Tracks against MusicBrainz + the Cover Art Archive. Path:
///
///   1. If the Track has a Spotify URI in <see cref="Track.ExternalId"/>,
///      look it up via MB's URL → recording-rels endpoint. This is the
///      reliable path for Spotify Connect since librespot only gives us
///      a title string, not artist.
///   2. Otherwise, if title + artist are both present, search
///      MB recordings by query string and pick the first hit's release.
///   3. Either way, fetch the front cover from coverartarchive.org for
///      the release we landed on.
///
/// All results are cached on disk via <see cref="MetadataCache"/> so a
/// repeating playlist doesn't re-hit the network. MB's free API is
/// limited to 1 req/sec (per their ToS); we serialize through a
/// semaphore and tick a stopwatch between requests.
/// </summary>
public sealed class MusicBrainzEnricher : IMetadataEnricher
{
    public string Id => "musicbrainz";

    private readonly HttpClient        _http;
    private readonly MetadataCache     _cache;
    private readonly SemaphoreSlim     _gate   = new(1, 1);
    private readonly Stopwatch         _sinceLast = Stopwatch.StartNew();

    public MusicBrainzEnricher(MetadataCache cache,
                               HttpClient? http = null,
                               string? contact = null)
    {
        _cache = cache;
        _http  = http ?? new HttpClient(new HttpClientHandler
        {
            AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate,
        })
        {
            Timeout = TimeSpan.FromSeconds(15),
        };

        // MB ToS requires a descriptive User-Agent that identifies the
        // application and provides contact info. The contact is
        // user-configurable; if blank, fall back to the project URL.
        var contactStr = string.IsNullOrWhiteSpace(contact)
            ? "https://github.com/dkatz/horizon-radio"
            : contact;
        var ua = $"HorizonRadio/0.1.0 ( {contactStr} )";
        if (!_http.DefaultRequestHeaders.UserAgent.TryParseAdd(ua))
        {
            _http.DefaultRequestHeaders.TryAddWithoutValidation("User-Agent", ua);
        }
    }

    private static void Log(string msg) => Debug.WriteLine($"[hzn-mb] {msg}");

    public async Task<Track?> EnrichAsync(Track track, CancellationToken ct)
    {
        // Build the cache key from whatever identifying info we have.
        // Spotify URI is the strongest; (artist, title) is the
        // fallback. Without either we can't enrich.
        string? queryKey =
            !string.IsNullOrEmpty(track.ExternalId) && track.ExternalId.StartsWith("spotify:track:")
                ? "uri=" + track.ExternalId
                : !string.IsNullOrEmpty(track.Title) && !string.IsNullOrEmpty(track.Artist)
                    ? $"text={track.Artist.ToLowerInvariant()}|{track.Title.ToLowerInvariant()}"
                    : null;

        if (queryKey == null) return null;

        var cacheKey = MetadataCache.Key(Id, queryKey);
        var hit      = _cache.TryGet(cacheKey);
        if (hit != null)
        {
            // Either a positive cache (fields filled in) or a negative
            // cache (all null) — both mean "don't re-query."
            return ApplyEntry(track, hit);
        }

        var entry = queryKey.StartsWith("uri=")
            ? await EnrichBySpotifyUriAsync(track.ExternalId!, ct).ConfigureAwait(false)
            : await EnrichByTextAsync(track.Artist, track.Title, ct).ConfigureAwait(false);

        if (entry == null)
        {
            _cache.PutMiss(cacheKey);
            return null;
        }
        _cache.Put(cacheKey, entry);
        return ApplyEntry(track, entry);
    }

    private static Track? ApplyEntry(Track t, MetadataCache.Entry e)
    {
        // Negative cache → nothing usable on the entry.
        if (e.Title == null && e.Artist == null && e.Album == null &&
            e.AlbumArt == null && e.Mbid == null) return null;

        // Prefer source's text when it was non-empty; MB's may be
        // canonical but the source's is what the user expects to see.
        return t with
        {
            Album    = !string.IsNullOrEmpty(t.Album)  ? t.Album  : e.Album,
            Artist   = !string.IsNullOrEmpty(t.Artist) ? t.Artist : e.Artist ?? "",
            AlbumArt = t.AlbumArt ?? e.AlbumArt,
        };
    }

    private async Task<MetadataCache.Entry?> EnrichByTextAsync(string artist, string title,
                                                               CancellationToken ct)
    {
        var query = $"artist:\"{EscapeLucene(artist)}\" AND recording:\"{EscapeLucene(title)}\"";
        var url   = $"https://musicbrainz.org/ws/2/recording/?query={Uri.EscapeDataString(query)}&fmt=json&limit=1";

        await ThrottleAsync(ct).ConfigureAwait(false);
        using var resp = await _http.GetAsync(url, ct).ConfigureAwait(false);
        if (!resp.IsSuccessStatusCode)
        {
            Log($"text search HTTP {(int)resp.StatusCode} for {artist} / {title}");
            return null;
        }

        using var stream = await resp.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        using var doc    = await JsonDocument.ParseAsync(stream, default, ct).ConfigureAwait(false);
        var root = doc.RootElement;

        if (!root.TryGetProperty("recordings", out var recordings) ||
            recordings.ValueKind != JsonValueKind.Array ||
            recordings.GetArrayLength() == 0) return null;

        var rec = recordings[0];
        string? canonicalTitle  = rec.TryGetProperty("title",         out var t) ? t.GetString() : null;
        string? canonicalArtist = rec.TryGetProperty("artist-credit", out var ac) &&
                                  ac.ValueKind == JsonValueKind.Array && ac.GetArrayLength() > 0 &&
                                  ac[0].TryGetProperty("name", out var nm) ? nm.GetString() : null;

        // Walk through this recording's releases looking for cover art.
        // MB doesn't tell us which release has art without asking CAA;
        // we just try the first few and stop at the first hit.
        if (!rec.TryGetProperty("releases", out var releases) ||
            releases.ValueKind != JsonValueKind.Array) return null;

        int maxTries = Math.Min(3, releases.GetArrayLength());
        for (int i = 0; i < maxTries; ++i)
        {
            var rel = releases[i];
            if (!rel.TryGetProperty("id",    out var mbidEl) ||
                !rel.TryGetProperty("title", out var albumEl)) continue;

            var mbid  = mbidEl.GetString();
            var album = albumEl.GetString();
            if (string.IsNullOrEmpty(mbid)) continue;

            var art = await FetchCoverArtAsync(mbid, ct).ConfigureAwait(false);
            if (art != null)
            {
                return new MetadataCache.Entry(
                    Title:    canonicalTitle,
                    Artist:   canonicalArtist,
                    Album:    album,
                    AlbumArt: art,
                    Mbid:     mbid);
            }
        }

        // No art found, but we still got canonical text — record it so
        // we don't re-query.
        return new MetadataCache.Entry(canonicalTitle, canonicalArtist, null, null, null);
    }

    private async Task<MetadataCache.Entry?> EnrichBySpotifyUriAsync(string spotifyUri,
                                                                     CancellationToken ct)
    {
        // Spotify URI -> MB URL entity -> recording relationship -> release -> art.
        // MB has Spotify URLs indexed against recordings via the
        // "stream for free" / "free streaming" URL relationships.
        var spotifyId  = spotifyUri.Substring("spotify:track:".Length);
        var resource   = $"https://open.spotify.com/track/{spotifyId}";
        var url        = $"https://musicbrainz.org/ws/2/url?resource={Uri.EscapeDataString(resource)}&inc=recording-rels&fmt=json";

        await ThrottleAsync(ct).ConfigureAwait(false);
        using var resp = await _http.GetAsync(url, ct).ConfigureAwait(false);
        if (!resp.IsSuccessStatusCode)
        {
            // 404 here = MB doesn't have this Spotify URL indexed,
            // which is common for newer / smaller releases. Treat as
            // "no enrichment" rather than retrying.
            Log($"uri lookup HTTP {(int)resp.StatusCode} for {spotifyUri}");
            return null;
        }

        using var stream = await resp.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        using var doc    = await JsonDocument.ParseAsync(stream, default, ct).ConfigureAwait(false);
        var root = doc.RootElement;
        if (!root.TryGetProperty("relations", out var rels) ||
            rels.ValueKind != JsonValueKind.Array ||
            rels.GetArrayLength() == 0) return null;

        // Pick the first recording relation. The recording's id needs a
        // separate lookup to get its releases.
        string? recordingMbid = null;
        foreach (var r in rels.EnumerateArray())
        {
            if (r.TryGetProperty("recording", out var rec) &&
                rec.TryGetProperty("id", out var idEl))
            {
                recordingMbid = idEl.GetString();
                if (!string.IsNullOrEmpty(recordingMbid)) break;
            }
        }
        if (string.IsNullOrEmpty(recordingMbid)) return null;

        var recUrl = $"https://musicbrainz.org/ws/2/recording/{recordingMbid}?inc=artist-credits+releases&fmt=json";
        await ThrottleAsync(ct).ConfigureAwait(false);
        using var resp2 = await _http.GetAsync(recUrl, ct).ConfigureAwait(false);
        if (!resp2.IsSuccessStatusCode) return null;

        using var stream2 = await resp2.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        using var doc2    = await JsonDocument.ParseAsync(stream2, default, ct).ConfigureAwait(false);
        var rroot = doc2.RootElement;

        string? canonicalTitle  = rroot.TryGetProperty("title",         out var t) ? t.GetString() : null;
        string? canonicalArtist = rroot.TryGetProperty("artist-credit", out var ac) &&
                                  ac.ValueKind == JsonValueKind.Array && ac.GetArrayLength() > 0 &&
                                  ac[0].TryGetProperty("name", out var nm) ? nm.GetString() : null;

        if (!rroot.TryGetProperty("releases", out var releases) ||
            releases.ValueKind != JsonValueKind.Array) return null;

        int maxTries = Math.Min(3, releases.GetArrayLength());
        for (int i = 0; i < maxTries; ++i)
        {
            var rel = releases[i];
            if (!rel.TryGetProperty("id",    out var mbidEl) ||
                !rel.TryGetProperty("title", out var albumEl)) continue;

            var mbid  = mbidEl.GetString();
            var album = albumEl.GetString();
            if (string.IsNullOrEmpty(mbid)) continue;

            var art = await FetchCoverArtAsync(mbid, ct).ConfigureAwait(false);
            if (art != null)
            {
                return new MetadataCache.Entry(canonicalTitle, canonicalArtist, album, art, mbid);
            }
        }

        return new MetadataCache.Entry(canonicalTitle, canonicalArtist, null, null, null);
    }

    private async Task<byte[]?> FetchCoverArtAsync(string releaseMbid, CancellationToken ct)
    {
        // CAA isn't behind the MB rate limit, but it can be slow / return
        // 404 / 503 (when MB hasn't indexed the release yet). 500-wide
        // thumbnail is the best size for our 180x180 HUD tile.
        var url = $"https://coverartarchive.org/release/{releaseMbid}/front-500";
        try
        {
            using var resp = await _http.GetAsync(url, ct).ConfigureAwait(false);
            if (!resp.IsSuccessStatusCode) return null;
            return await resp.Content.ReadAsByteArrayAsync(ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            Log($"CAA {releaseMbid}: {ex.Message}");
            return null;
        }
    }

    private async Task ThrottleAsync(CancellationToken ct)
    {
        // Serialize and ensure ≥1100 ms between requests. The extra
        // 100 ms gives a buffer against clock drift on MB's side that
        // would otherwise occasionally return 503.
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var elapsed = _sinceLast.Elapsed;
            var min     = TimeSpan.FromMilliseconds(1100);
            if (elapsed < min)
            {
                try { await Task.Delay(min - elapsed, ct).ConfigureAwait(false); }
                catch (OperationCanceledException) { throw; }
            }
            _sinceLast.Restart();
        }
        finally
        {
            _gate.Release();
        }
    }

    private static string EscapeLucene(string s)
    {
        // Minimum escaping to keep MB's query parser happy.
        var sb = new System.Text.StringBuilder(s.Length + 4);
        foreach (var c in s)
        {
            if (c == '"' || c == '\\') sb.Append('\\');
            sb.Append(c);
        }
        return sb.ToString();
    }
}
