using System.Diagnostics;
using System.Net;
using System.Text.Json;

namespace HorizonRadio.Core.Metadata.MusicBrainz;

public sealed class MusicBrainzProvider : IMetadataProvider
{
    public string Id => "musicbrainz";

    private readonly HttpClient _http;
    private readonly bool _ownsHttp;
    private readonly MetadataCache _cache;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly Stopwatch _sinceLast = Stopwatch.StartNew();

    public MusicBrainzProvider(MetadataCache cache,
                               HttpClient? http = null,
                               string? contact = null)
    {
        _cache = cache;
        _ownsHttp = http is null;
        _http = http ?? new HttpClient(new HttpClientHandler
        {
            AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate,
        })
        {
            Timeout = TimeSpan.FromSeconds(15),
        };

        // MB ToS requires a descriptive User-Agent with contact info.
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

    public async Task<MetadataContribution?> ContributeAsync(MetadataQuery query, CancellationToken ct)
    {
        string? queryKey =
            !string.IsNullOrEmpty(query.ExternalId) &&
            query.ExternalId.StartsWith("spotify:track:", StringComparison.Ordinal)
                ? "uri=" + query.ExternalId
                : !string.IsNullOrEmpty(query.Title) && !string.IsNullOrEmpty(query.Artist)
                    ? $"text={query.Artist.ToLowerInvariant()}|{query.Title.ToLowerInvariant()}"
                    : null;

        if (queryKey == null) return null;

        var cacheKey = MetadataCache.Key(Id, queryKey);
        var hit = _cache.TryGet(cacheKey);
        if (hit != null) return ToContribution(hit);

        var entry = queryKey.StartsWith("uri=", StringComparison.Ordinal)
            ? await EnrichBySpotifyUriAsync(query.ExternalId!, ct).ConfigureAwait(false)
            : await EnrichByTextAsync(query.Artist, query.Title, ct).ConfigureAwait(false);

        if (entry == null)
        {
            _cache.PutMiss(cacheKey);
            return null;
        }
        _cache.Put(cacheKey, entry);
        return ToContribution(entry);
    }

    private static MetadataContribution? ToContribution(MetadataCache.Entry e)
    {
        var c = new MetadataContribution(e.Title, e.Artist, e.Album, e.AlbumArt, e.Year);
        return c.IsEmpty ? null : c;
    }

    private async Task<MetadataCache.Entry?> EnrichByTextAsync(string artist, string title,
                                                               CancellationToken ct)
    {
        // Clean tag noise out of the query so a "[Hatsune Miku]" vocalist tag can't leak
        // a stray word ("Miku") into the search.
        var cleanTitle = SearchTerms.CleanForSearch(title);
        if (string.IsNullOrEmpty(cleanTitle)) return null;
        var cleanArtist = SearchTerms.CleanForSearch(artist);

        var query = string.IsNullOrEmpty(cleanArtist)
            ? $"recording:\"{EscapeLucene(cleanTitle)}\""
            : $"artist:\"{EscapeLucene(cleanArtist)}\" AND recording:\"{EscapeLucene(cleanTitle)}\"";
        var url = $"https://musicbrainz.org/ws/2/recording/?query={Uri.EscapeDataString(query)}&fmt=json&limit=5";

        await ThrottleAsync(ct).ConfigureAwait(false);
        using var resp = await _http.GetAsync(url, ct).ConfigureAwait(false);
        if (!resp.IsSuccessStatusCode)
        {
            Log($"text search HTTP {(int)resp.StatusCode} for {artist} / {title}");
            return null;
        }

        using var stream = await resp.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        using var doc = await JsonDocument.ParseAsync(stream, default, ct).ConfigureAwait(false);
        var root = doc.RootElement;

        if (!root.TryGetProperty("recordings", out var recordings) ||
            recordings.ValueKind != JsonValueKind.Array ||
            recordings.GetArrayLength() == 0) return null;

        // MB ranks by its own relevance and will happily return an unrelated recording for
        // a loose query; score each against the request (title-first) and take the best
        // that actually matches, so we never attach a wrong album's art.
        JsonElement rec = default;
        string? canonicalTitle = null, canonicalArtist = null;
        double bestScore = double.NegativeInfinity;
        foreach (var candidate in recordings.EnumerateArray())
        {
            var rt = candidate.TryGetProperty("title", out var t) ? t.GetString() : null;
            if (rt is null) continue;
            var ra = candidate.TryGetProperty("artist-credit", out var ac) &&
                     ac.ValueKind == JsonValueKind.Array && ac.GetArrayLength() > 0 &&
                     ac[0].TryGetProperty("name", out var nm) ? nm.GetString() : null;

            if (SearchTerms.MatchScore(title, artist, rt, ra) is not { } score || score <= bestScore) continue;
            bestScore = score;
            rec = candidate;
            canonicalTitle = rt;
            canonicalArtist = ra;
        }
        if (canonicalTitle is null) return null; // nothing matched the request

        if (!rec.TryGetProperty("releases", out var releases) ||
            releases.ValueKind != JsonValueKind.Array)
            return new MetadataCache.Entry(canonicalTitle, canonicalArtist, null, null, null);

        // MB doesn't indicate which release has art; probe the first few via CAA.
        int maxTries = Math.Min(3, releases.GetArrayLength());
        for (int i = 0; i < maxTries; ++i)
        {
            var rel = releases[i];
            if (!rel.TryGetProperty("id", out var mbidEl) ||
                !rel.TryGetProperty("title", out var albumEl)) continue;

            var mbid = mbidEl.GetString();
            var album = albumEl.GetString();
            if (string.IsNullOrEmpty(mbid)) continue;

            var art = await FetchCoverArtAsync(mbid, ct).ConfigureAwait(false);
            if (art != null)
            {
                return new MetadataCache.Entry(
                    Title: canonicalTitle,
                    Artist: canonicalArtist,
                    Album: album,
                    AlbumArt: art,
                    Mbid: mbid);
            }
        }

        return new MetadataCache.Entry(canonicalTitle, canonicalArtist, null, null, null);
    }

    private async Task<MetadataCache.Entry?> EnrichBySpotifyUriAsync(string spotifyUri,
                                                                     CancellationToken ct)
    {
        var spotifyId = spotifyUri.Substring("spotify:track:".Length);
        var resource = $"https://open.spotify.com/track/{spotifyId}";
        var url = $"https://musicbrainz.org/ws/2/url?resource={Uri.EscapeDataString(resource)}&inc=recording-rels&fmt=json";

        await ThrottleAsync(ct).ConfigureAwait(false);
        using var resp = await _http.GetAsync(url, ct).ConfigureAwait(false);
        if (!resp.IsSuccessStatusCode)
        {
            // 404 = MB hasn't indexed this Spotify URL; common for new/small releases.
            Log($"uri lookup HTTP {(int)resp.StatusCode} for {spotifyUri}");
            return null;
        }

        using var stream = await resp.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        using var doc = await JsonDocument.ParseAsync(stream, default, ct).ConfigureAwait(false);
        var root = doc.RootElement;
        if (!root.TryGetProperty("relations", out var rels) ||
            rels.ValueKind != JsonValueKind.Array ||
            rels.GetArrayLength() == 0) return null;

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
        using var doc2 = await JsonDocument.ParseAsync(stream2, default, ct).ConfigureAwait(false);
        var rroot = doc2.RootElement;

        string? canonicalTitle = rroot.TryGetProperty("title", out var t) ? t.GetString() : null;
        string? canonicalArtist = rroot.TryGetProperty("artist-credit", out var ac) &&
                                  ac.ValueKind == JsonValueKind.Array && ac.GetArrayLength() > 0 &&
                                  ac[0].TryGetProperty("name", out var nm) ? nm.GetString() : null;

        if (!rroot.TryGetProperty("releases", out var releases) ||
            releases.ValueKind != JsonValueKind.Array) return null;

        int maxTries = Math.Min(3, releases.GetArrayLength());
        for (int i = 0; i < maxTries; ++i)
        {
            var rel = releases[i];
            if (!rel.TryGetProperty("id", out var mbidEl) ||
                !rel.TryGetProperty("title", out var albumEl)) continue;

            var mbid = mbidEl.GetString();
            var album = albumEl.GetString();
            if (string.IsNullOrEmpty(mbid)) continue;

            var art = await FetchCoverArtAsync(mbid, ct).ConfigureAwait(false);
            if (art != null)
                return new MetadataCache.Entry(canonicalTitle, canonicalArtist, album, art, mbid);
        }

        return new MetadataCache.Entry(canonicalTitle, canonicalArtist, null, null, null);
    }

    private async Task<byte[]?> FetchCoverArtAsync(string releaseMbid, CancellationToken ct)
    {
        // CAA front-500 is the right size for the 180×180 HUD tile.
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
        // MB ToS: 1 req/sec. Extra 100 ms buffers against clock drift
        // that otherwise occasionally returns 503.
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var elapsed = _sinceLast.Elapsed;
            var min = TimeSpan.FromMilliseconds(1100);
            if (elapsed < min)
                await Task.Delay(min - elapsed, ct).ConfigureAwait(false);
            _sinceLast.Restart();
        }
        finally
        {
            _gate.Release();
        }
    }

    private static string EscapeLucene(string s)
    {
        var sb = new System.Text.StringBuilder(s.Length + 4);
        foreach (var c in s)
        {
            if (c == '"' || c == '\\') sb.Append('\\');
            sb.Append(c);
        }
        return sb.ToString();
    }

    public ValueTask DisposeAsync()
    {
        _gate.Dispose();
        if (_ownsHttp) _http.Dispose();
        return ValueTask.CompletedTask;
    }
}
