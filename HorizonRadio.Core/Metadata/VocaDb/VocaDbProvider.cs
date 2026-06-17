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

namespace HorizonRadio.Core.Metadata.VocaDb;

/// <summary>
/// Metadata via VocaDB (vocadb.net) — the community database for Vocaloid / UTAU / CeVIO /
/// SynthV music. Keyless, and it covers the huge swath of producer tracks that live only on
/// Niconico/YouTube and are absent from iTunes/Spotify/MusicBrainz — exactly the radio gap.
///
/// VocaDB's free-text search matches the song NAME only, which buries a track when the title
/// is common ("Beyond the Sky"). So when we know the broadcast artist we first resolve it to
/// a VocaDB artist (preferring the producer/circle, exact name) and search WITHIN that
/// artist's songs (<c>artistId[]</c>) — which surfaces the right track directly. A plain
/// name search is the fallback when no artist resolves. Candidates are scored title-first
/// with <see cref="SearchTerms.MatchScore"/>.
///
/// Artwork is the song's representative image — usually the Niconico/YouTube video thumbnail
/// (16:9, not square album art), so this provider is ordered after the square-cover ones and
/// fills in only when they miss.
/// </summary>
public sealed class VocaDbProvider : IMetadataProvider
{
    public string Id => "vocadb";

    private const string Base = "https://vocadb.net/api";

    private readonly MetadataCache _cache;
    private readonly HttpClient _http;
    private readonly bool _ownsHttp;
    private readonly RateGate _rate = new(TimeSpan.FromMilliseconds(300));

    public VocaDbProvider(MetadataCache cache, HttpClient? http = null)
    {
        _cache = cache;
        _ownsHttp = http is null;
        _http = http ?? new HttpClient { Timeout = TimeSpan.FromSeconds(15) };
        if (!_http.DefaultRequestHeaders.UserAgent.TryParseAdd("HorizonRadio/0.5 (internet-radio metadata)"))
            _http.DefaultRequestHeaders.TryAddWithoutValidation("User-Agent", "HorizonRadio/0.5");
    }

    private static void Log(string msg) => Debug.WriteLine($"[hzn-vocadb] {msg}");

    public async Task<MetadataContribution?> ContributeAsync(MetadataQuery query, CancellationToken ct)
    {
        var title = SearchTerms.CleanForSearch(query.Title);
        if (string.IsNullOrEmpty(title)) return null;
        var artist = SearchTerms.CleanForSearch(query.Artist);

        var cacheKey = MetadataCache.Key(Id, $"{artist.ToLowerInvariant()}|{title.ToLowerInvariant()}");
        var hit = _cache.TryGet(cacheKey);
        if (hit != null) return ToContribution(hit);

        Match? match = null;
        var capture = MetadataTrace.NewCapture();

        // Artist-scoped search first (precise): resolve the broadcast artist to VocaDB
        // artist id(s), then search within their songs.
        if (!string.IsNullOrEmpty(artist))
        {
            foreach (var artistId in await ResolveArtistIdsAsync(artist, ct).ConfigureAwait(false))
            {
                match = await SearchSongsAsync(title, query.Title, query.Artist, artistId, capture, ct).ConfigureAwait(false);
                if (match != null) break;
            }
        }

        // Fallback: plain name search (no artist known, or none of the artist's songs matched).
        match ??= await SearchSongsAsync(title, query.Title, query.Artist, artistId: null, capture, ct).ConfigureAwait(false);

        MetadataTrace.ProviderSearch(Id, string.IsNullOrEmpty(artist) ? title : $"{artist} {title}", capture);
        if (match is null) { _cache.PutMiss(cacheKey); return null; }

        // Try the image URLs in order: VocaDB's urlOriginal is often a YouTube hqdefault that 404s
        // when the source video is gone, while the urlThumb/thumbUrl mirror (Niconico/Bilibili) still
        // resolves — so fall through to it rather than giving up on the first dead link.
        var art = await DownloadFirstAsync(match.ArtUrls, ct).ConfigureAwait(false);

        // A Remaster / re-upload entry often carries no art of its own ("Re-Confliction"); borrow the
        // original version's image, which is the same song's cover.
        if (art is not { Length: > 0 } && match.OriginalVersionId is { } originalId)
        {
            var root = await GetJsonAsync($"{Base}/songs/{originalId}?fields=MainPicture,ThumbUrl&lang=Default", ct).ConfigureAwait(false);
            if (root is { } r) art = await DownloadFirstAsync(PickArt(r), ct).ConfigureAwait(false);
        }

        var entry = new MetadataCache.Entry(match.Name, match.Artist, Album: null, AlbumArt: art, Mbid: null,
            Year: match.Year, Pvs: match.Pvs);
        _cache.Put(cacheKey, entry);
        return ToContribution(entry);
    }

    private static MetadataContribution? ToContribution(MetadataCache.Entry e)
    {
        var c = new MetadataContribution(e.Title, e.Artist, e.Album, e.AlbumArt, e.Year, e.Pvs);
        return c.IsEmpty ? null : c;
    }

    // -- artist resolution --

    private async Task<IReadOnlyList<int>> ResolveArtistIdsAsync(string artist, CancellationToken ct)
    {
        // Exact first — it surfaces the real artist without Auto's fuzzy substring hits. But a
        // romanized broadcast name often doesn't EXACTLY match VocaDB's (hyphen/case/spacing, e.g.
        // "Itachima-p" vs "ItachimaP"), so fall back to Auto when Exact finds nothing. The
        // type-ranking here plus the downstream scoped, title-matched song search keep a looser
        // artist hit from producing a false song match. lang=English matches the romanized name.
        foreach (var mode in (string[])["Exact", "Auto"])
        {
            var url = $"{Base}/artists?query={Uri.EscapeDataString(artist)}&maxResults=10&nameMatchMode={mode}&lang=English";
            var root = await GetJsonAsync(url, ct).ConfigureAwait(false);
            if (root is { } r && SelectArtistIds(r, max: 2) is { Count: > 0 } ids) return ids;
        }
        return [];
    }

    /// <summary>Rank exact-name artist hits by how likely they are the song's main artist
    /// (producer/circle/band over an illustrator/animator that merely shares the name) and
    /// return the top ids. Pure JSON-in for unit testing.</summary>
    internal static IReadOnlyList<int> SelectArtistIds(JsonElement root, int max)
    {
        if (!root.TryGetProperty("items", out var items) || items.ValueKind != JsonValueKind.Array)
            return [];

        var ranked = new List<(int rank, int order, int id)>();
        int order = 0;
        foreach (var a in items.EnumerateArray())
        {
            if (!a.TryGetProperty("id", out var idEl) || idEl.ValueKind != JsonValueKind.Number) continue;
            ranked.Add((ArtistTypeRank(Str(a, "artistType")), order++, idEl.GetInt32()));
        }
        // Preferred type first; original relevance order breaks ties.
        return [.. ranked.OrderBy(x => x.rank).ThenBy(x => x.order).Take(max).Select(x => x.id)];
    }

    // Lower = more likely to be the track's credited artist.
    private static int ArtistTypeRank(string? type) => type switch
    {
        "Producer" => 0,
        "Circle" or "Band" => 1,
        "OtherGroup" => 2,
        "Vocaloid" or "UTAU" or "CeVIO" or "SynthesizerV" or "OtherVoiceSynthesizer"
            or "OtherVocalist" or "Utaite" or "OtherIndividual" => 3,
        "Animator" or "Illustrator" or "Lyricist" => 8, // share names but rarely the main artist
        _ => 5,
    };

    // -- song search --

    private async Task<Match?> SearchSongsAsync(string nameQuery, string rawTitle, string? rawArtist, int? artistId,
        ICollection<MetadataTrace.CatalogCandidate>? sink, CancellationToken ct)
    {
        // lang=Default keeps the artistString in its native script (e.g. "文脈 feat. GUMI") rather
        // than translating it (lang=English turns 文脈 into "Context"), which is what lets the
        // cross-script artist check in SearchTerms.MatchScore bridge a romaji broadcast name to a
        // kanji catalog name. Title romanization is no longer needed from lang here because Names
        // brings back every language variant of the title for SelectMatch to score against.
        //
        // Trade-off: for an artist VocaDB *can* romanize (ばらっげ -> "BarrageP"), lang=English used
        // to give a direct artist-token match that also carried 1-word titles. Under lang=Default
        // that artist is native, so a 1-word title now only resolves via the artist-scoped path
        // (ResolveArtistIds), not the cross-script branch (which requires a multi-word title). We
        // accept that: 1-word titles are generic/cover-prone (the single-token guard already
        // distrusts them), and lang=English's mistranslation of name-like kanji was the worse bug.
        var url = $"{Base}/songs?query={Uri.EscapeDataString(nameQuery)}"
                + "&maxResults=10&nameMatchMode=Auto&lang=Default&fields=Artists,MainPicture,ThumbUrl,Names,Albums,PVs";
        // artistId[]= scopes the search to one artist; the array syntax is what the REST API needs.
        // A scoped hit has its artist already confirmed, so the title alone qualifies it.
        bool artistConfirmed = artistId.HasValue;
        if (artistId is { } id) url += $"&artistId%5B%5D={id}";

        var root = await GetJsonAsync(url, ct).ConfigureAwait(false);
        return root is { } r ? SelectMatch(r, rawTitle, rawArtist, artistConfirmed, sink) : null;
    }

    /// <summary>Pick the best-scoring song whose name (and, unless <paramref name="artistConfirmed"/>,
    /// artist) clears the match guard, and lift its representative image URL + year. Pure JSON-in
    /// for unit testing without HTTP. <paramref name="sink"/>, when supplied, collects every scored
    /// song (best score across its name variants) for the diagnostics trace.</summary>
    internal static Match? SelectMatch(JsonElement root, string queryTitle, string? queryArtist,
        bool artistConfirmed = false, ICollection<MetadataTrace.CatalogCandidate>? sink = null)
    {
        if (!root.TryGetProperty("items", out var items) || items.ValueKind != JsonValueKind.Array)
            return null;

        // For a title-only query, reject a widely-covered/ambiguous title rather than attaching a
        // random cover's art. Inert when an artist is present — including the artist-scoped path,
        // where artistConfirmed always comes with a non-empty queryArtist.
        var titleGuard = new TitleOnlyGuard(queryArtist);
        Match? best = null;
        double bestScore = double.NegativeInfinity;
        foreach (var s in items.EnumerateArray())
        {
            var name = Str(s, "name");
            if (name is null) continue;
            var artist = Str(s, "artistString");

            // Score against every language variant of the title and keep the best — the broadcast
            // title may be in a different script than the entry's primary (English) name.
            double? scoreForSong = null;
            foreach (var variant in NameVariants(s, name))
                if (SearchTerms.MatchScore(queryTitle, queryArtist, variant, artist, artistConfirmed) is { } sc &&
                    (scoreForSong is null || sc > scoreForSong))
                    scoreForSong = sc;

            sink?.Add(new(name, artist, null, scoreForSong));
            if (scoreForSong is { }) titleGuard.Observe(artist);
            if (scoreForSong is not { } score) continue;

            // Prefer a strictly higher score; on a tie, prefer a candidate that actually has its own
            // art (a real thumbnail) so an equal-scoring sibling with art (e.g. the Kagamine Rin
            // version) wins over an image-less entry (the GUMI version) instead of leaving the tile
            // blank. The album cover is added to the WINNER afterwards, so it can't skew this proxy.
            var ownArt = PickArt(s);
            bool better = score > bestScore
                || (score == bestScore && best is not null && best.ArtUrls.Count == 0 && ownArt.Count > 0);
            if (!better) continue;

            bestScore = score;
            best = new Match(name, artist, ownArt, ParseYear(Str(s, "publishDate")), OriginalId(s),
                SelectAlbumId(s), SelectPvs(s));
        }
        if (titleGuard.IsAmbiguous) return null;

        // Prefer a real square album cover over the winner's own 16:9 video thumbnail: prepend the
        // album image URLs ahead of its thumbnail (DownloadFirstAsync tries them in order and falls
        // through to the thumbnail if the album image is missing). Both extensions are offered
        // because VocaDB serves some covers only as .png.
        if (best is { AlbumId: { } albumId })
            best = best with { ArtUrls = [.. AlbumCoverUrls(albumId), .. best.ArtUrls] };
        return best;
    }

    // VocaDB's link from a Remaster / re-upload to the song it derives from (0 = none). Lets an
    // art-less remaster borrow the original version's image.
    private static int? OriginalId(JsonElement song) =>
        song.TryGetProperty("originalVersionId", out var p) && p.ValueKind == JsonValueKind.Number
            && p.TryGetInt32(out var id) && id > 0 ? id : null;

    // The static URLs for an album's square cover. The mainOrig/<id>.<ext> pattern is stable, but the
    // extension follows the source image — VocaDB serves some covers only as .png — so we offer both
    // and let DownloadFirstAsync take whichever resolves (and fall through to the thumbnail if neither).
    private static string[] AlbumCoverUrls(int albumId) =>
    [
        $"https://static.vocadb.net/img/Album/mainOrig/{albumId}.jpg",
        $"https://static.vocadb.net/img/Album/mainOrig/{albumId}.png",
    ];

    /// <summary>Choose which of a song's albums to take cover art from: among albums that actually
    /// have a cover, prefer a real release (Album/Single/EP) over a compilation/video, then the
    /// earliest release date, then VocaDB's own order. Pure JSON-in for unit testing.</summary>
    internal static int? SelectAlbumId(JsonElement song)
    {
        if (!song.TryGetProperty("albums", out var albums) || albums.ValueKind != JsonValueKind.Array)
            return null;

        (int Rank, long Date, int Order, int Id)? best = null;
        var order = 0;
        foreach (var a in albums.EnumerateArray())
        {
            order++;
            if (string.IsNullOrEmpty(Str(a, "coverPictureMime"))) continue; // no cover → skip
            if (!a.TryGetProperty("id", out var idEl) || idEl.ValueKind != JsonValueKind.Number
                || !idEl.TryGetInt32(out var id)) continue;

            var cand = (DiscTypeRank(Str(a, "discType")), ReleaseSortKey(a), order, id);
            // Lower rank, then earlier date, then earlier order.
            if (best is null || cand.CompareTo((best.Value.Rank, best.Value.Date, best.Value.Order, best.Value.Id)) < 0)
                best = cand;
        }
        return best?.Id;
    }

    // Lower = more representative of the song's original release.
    private static int DiscTypeRank(string? discType) => discType switch
    {
        "Album" or "Single" or "EP" => 0,
        "SplitAlbum" => 1,
        "Compilation" => 2,
        _ => 3, // Video, Artbook, Other, Unknown, …
    };

    // yyyymmdd for earliest-first sorting; a missing/empty date sorts last so dated releases win.
    // A partial date (year known, month/day absent — common on VocaDB) defaults to the START of the
    // period (Jan 1), so a year-only original beats a fully-dated later-month reissue of that year.
    private static long ReleaseSortKey(JsonElement album)
    {
        if (!album.TryGetProperty("releaseDate", out var d) || d.ValueKind != JsonValueKind.Object)
            return long.MaxValue;
        if (d.TryGetProperty("isEmpty", out var e) && e.ValueKind == JsonValueKind.True)
            return long.MaxValue;
        int y = IntOr(d, "year", 9999), m = IntOr(d, "month", 1), day = IntOr(d, "day", 1);
        return (y * 10000L) + (m * 100L) + day;
    }

    private static int IntOr(JsonElement e, string name, int fallback) =>
        e.TryGetProperty(name, out var p) && p.ValueKind == JsonValueKind.Number && p.TryGetInt32(out var i)
            ? i : fallback;

    // VocaDB PV "service" values we can hand to the yt-dlp engine, mapped to a friendly label.
    private static readonly Dictionary<string, string> StreamableServices =
        new(StringComparer.Ordinal)
        {
            ["Youtube"] = "YouTube",
            ["NicoNicoDouga"] = "Niconico",
            ["Bilibili"] = "Bilibili",
            ["SoundCloud"] = "SoundCloud",
            ["Vimeo"] = "Vimeo",
            ["Bandcamp"] = "Bandcamp",
        };

    /// <summary>The song's promotion-video links worth keeping as playable sources: non-disabled,
    /// on a yt-dlp-streamable service, the best PV per service (Original &gt; Reprint &gt; Other),
    /// in first-seen service order. Pure JSON-in for unit testing.</summary>
    internal static IReadOnlyList<PlayableRef> SelectPvs(JsonElement song)
    {
        if (!song.TryGetProperty("pvs", out var pvs) || pvs.ValueKind != JsonValueKind.Array)
            return [];

        var bestByService = new Dictionary<string, (int Rank, string Url, string Display)>(StringComparer.Ordinal);
        var order = new List<string>();
        foreach (var pv in pvs.EnumerateArray())
        {
            if (pv.TryGetProperty("disabled", out var dis) && dis.ValueKind == JsonValueKind.True) continue;
            var service = Str(pv, "service");
            if (service is null || !StreamableServices.TryGetValue(service, out var display)) continue;
            var pvUrl = Str(pv, "url");
            if (string.IsNullOrEmpty(pvUrl)) continue;

            var rank = PvTypeRank(Str(pv, "pvType"));
            if (!bestByService.TryGetValue(service, out var cur)) { bestByService[service] = (rank, pvUrl!, display); order.Add(service); }
            else if (rank < cur.Rank) bestByService[service] = (rank, pvUrl!, display);
        }
        return [.. order.Select(s => new PlayableRef(bestByService[s].Display, bestByService[s].Url))];
    }

    // Lower = a more canonical upload (the official original beats a reprint beats a random copy).
    private static int PvTypeRank(string? pvType) => pvType switch
    {
        "Original" => 0,
        "Reprint" => 1,
        _ => 2,
    };

    // Download the first URL that yields bytes, or null if none do (each is best-effort).
    private async Task<byte[]?> DownloadFirstAsync(IReadOnlyList<string> urls, CancellationToken ct)
    {
        foreach (var url in urls)
        {
            var bytes = await ImageDownload.TryGetAsync(_http, url, ct).ConfigureAwait(false);
            if (bytes is { Length: > 0 }) return bytes;
        }
        return null;
    }

    // All candidate image URLs for a song, best first: the original picture, then its thumbnail
    // mirror, then the song thumbnail. Distinct and non-empty. Returned as a list (not a single URL)
    // so a dead urlOriginal can fall through to a working mirror at download time.
    private static List<string> PickArt(JsonElement song)
    {
        var urls = new List<string>();
        void Add(string? u) { if (!string.IsNullOrEmpty(u) && !urls.Contains(u)) urls.Add(u); }
        if (song.TryGetProperty("mainPicture", out var mp) && mp.ValueKind == JsonValueKind.Object)
        {
            Add(Str(mp, "urlOriginal"));
            Add(Str(mp, "urlThumb"));
        }
        Add(Str(song, "thumbUrl"));
        return urls;
    }

    /// <summary>The song's primary name plus every alternate-language name (from the <c>Names</c>
    /// field): so a query title in one script matches an entry surfaced in another.</summary>
    private static IEnumerable<string> NameVariants(JsonElement song, string primary)
    {
        yield return primary;
        if (song.TryGetProperty("names", out var names) && names.ValueKind == JsonValueKind.Array)
            foreach (var n in names.EnumerateArray())
                if (Str(n, "value") is { Length: > 0 } v &&
                    !string.Equals(v, primary, StringComparison.Ordinal))
                    yield return v;
    }

    private async Task<JsonElement?> GetJsonAsync(string url, CancellationToken ct)
    {
        await _rate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            using var resp = await _http.GetAsync(url, ct).ConfigureAwait(false);
            if (!resp.IsSuccessStatusCode) { Log($"HTTP {(int)resp.StatusCode}: {url}"); return null; }
            using var stream = await resp.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
            using var doc = await JsonDocument.ParseAsync(stream, default, ct).ConfigureAwait(false);
            return doc.RootElement.Clone(); // outlive the disposed document
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception ex) { Log($"request failed: {ex.Message}"); return null; }
    }

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

    internal sealed record Match(string Name, string? Artist, IReadOnlyList<string> ArtUrls, int? Year,
        int? OriginalVersionId = null, int? AlbumId = null, IReadOnlyList<PlayableRef>? Pvs = null);
}
