using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Linq;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

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

        // Artist-scoped search first (precise): resolve the broadcast artist to VocaDB
        // artist id(s), then search within their songs.
        if (!string.IsNullOrEmpty(artist))
        {
            foreach (var artistId in await ResolveArtistIdsAsync(artist, ct).ConfigureAwait(false))
            {
                match = await SearchSongsAsync(title, query.Title, query.Artist, artistId, ct).ConfigureAwait(false);
                if (match != null) break;
            }
        }

        // Fallback: plain name search (no artist known, or none of the artist's songs matched).
        match ??= await SearchSongsAsync(title, query.Title, query.Artist, artistId: null, ct).ConfigureAwait(false);

        if (match is null) { _cache.PutMiss(cacheKey); return null; }

        var art = match.ArtUrl != null ? await ImageDownload.TryGetAsync(_http, match.ArtUrl, ct).ConfigureAwait(false) : null;
        var entry = new MetadataCache.Entry(match.Name, match.Artist, Album: null, AlbumArt: art, Mbid: null, Year: match.Year);
        _cache.Put(cacheKey, entry);
        return ToContribution(entry);
    }

    private static MetadataContribution? ToContribution(MetadataCache.Entry e)
    {
        var c = new MetadataContribution(e.Title, e.Artist, e.Album, e.AlbumArt, e.Year);
        return c.IsEmpty ? null : c;
    }

    // -- artist resolution --

    private async Task<IReadOnlyList<int>> ResolveArtistIdsAsync(string artist, CancellationToken ct)
    {
        // Exact name match surfaces the real artist (Auto buries a producer under fuzzy
        // substring hits); lang=English to match the romanized broadcast name.
        var url = $"{Base}/artists?query={Uri.EscapeDataString(artist)}&maxResults=10&nameMatchMode=Exact&lang=English";
        var root = await GetJsonAsync(url, ct).ConfigureAwait(false);
        return root is { } r ? SelectArtistIds(r, max: 2) : [];
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

    private async Task<Match?> SearchSongsAsync(string nameQuery, string rawTitle, string? rawArtist, int? artistId, CancellationToken ct)
    {
        var url = $"{Base}/songs?query={Uri.EscapeDataString(nameQuery)}"
                + "&maxResults=10&nameMatchMode=Auto&lang=English&fields=Artists,MainPicture,ThumbUrl";
        // artistId[]= scopes the search to one artist; the array syntax is what the REST API needs.
        // A scoped hit has its artist already confirmed, so the title alone qualifies it.
        bool artistConfirmed = artistId.HasValue;
        if (artistId is { } id) url += $"&artistId%5B%5D={id}";

        var root = await GetJsonAsync(url, ct).ConfigureAwait(false);
        return root is { } r ? SelectMatch(r, rawTitle, rawArtist, artistConfirmed) : null;
    }

    /// <summary>Pick the best-scoring song whose name (and, unless <paramref name="artistConfirmed"/>,
    /// artist) clears the match guard, and lift its representative image URL + year. Pure JSON-in
    /// for unit testing without HTTP.</summary>
    internal static Match? SelectMatch(JsonElement root, string queryTitle, string? queryArtist, bool artistConfirmed = false)
    {
        if (!root.TryGetProperty("items", out var items) || items.ValueKind != JsonValueKind.Array)
            return null;

        Match? best = null;
        double bestScore = double.NegativeInfinity;
        foreach (var s in items.EnumerateArray())
        {
            var name = Str(s, "name");
            if (name is null) continue;
            var artist = Str(s, "artistString");

            if (SearchTerms.MatchScore(queryTitle, queryArtist, name, artist, artistConfirmed) is not { } score) continue;
            if (score <= bestScore) continue;

            bestScore = score;
            best = new Match(name, artist, PickArt(s), ParseYear(Str(s, "publishDate")));
        }
        return best;
    }

    // Prefer the original image; fall back to the thumbnail. Both are usually the song's
    // Niconico/YouTube video thumbnail.
    private static string? PickArt(JsonElement song)
    {
        if (song.TryGetProperty("mainPicture", out var mp) && mp.ValueKind == JsonValueKind.Object)
            return Str(mp, "urlOriginal") ?? Str(mp, "urlThumb") ?? Str(song, "thumbUrl");
        return Str(song, "thumbUrl");
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

    internal sealed record Match(string Name, string? Artist, string? ArtUrl, int? Year);
}
