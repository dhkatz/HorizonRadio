using System;
using System.Diagnostics;
using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using HorizonRadio.Core.Models;
using SpotifyAPI.Web;

namespace HorizonRadio.Core.Metadata.Spotify;

public sealed class SpotifyProvider : IMetadataProvider
{
    public string Id => "spotify";

    private readonly MetadataCache _cache;
    private readonly string _clientId;
    private readonly string _clientSecret;
    private readonly HttpClient _httpForArt = new() { Timeout = TimeSpan.FromSeconds(15) };
    private readonly SemaphoreSlim _initGate = new(1, 1);
    private SpotifyClient? _client;

    public SpotifyProvider(MetadataCache cache, string clientId, string clientSecret)
    {
        _cache = cache;
        _clientId = clientId;
        _clientSecret = clientSecret;
    }

    private static void Log(string msg) => Debug.WriteLine($"[hzn-spotify-mb] {msg}");

    public async Task<Track?> EnrichAsync(Track track, CancellationToken ct)
    {
        string? queryKey =
            !string.IsNullOrEmpty(track.ExternalId) &&
            track.ExternalId.StartsWith("spotify:track:", StringComparison.Ordinal)
                ? "uri=" + track.ExternalId
                : !string.IsNullOrEmpty(track.Title) && !string.IsNullOrEmpty(track.Artist)
                    ? $"text={track.Artist.ToLowerInvariant()}|{track.Title.ToLowerInvariant()}"
                    : null;
        if (queryKey == null) return null;

        var cacheKey = MetadataCache.Key(Id, queryKey);
        var hit = _cache.TryGet(cacheKey);
        if (hit != null) return ApplyEntry(track, hit);

        var entry = queryKey.StartsWith("uri=", StringComparison.Ordinal)
            ? await EnrichByUriAsync(track.ExternalId!, ct).ConfigureAwait(false)
            : await EnrichByTextAsync(track.Artist, track.Title, ct).ConfigureAwait(false);

        if (entry == null) { _cache.PutMiss(cacheKey); return null; }
        _cache.Put(cacheKey, entry);
        return ApplyEntry(track, entry);
    }

    private static Track? ApplyEntry(Track t, MetadataCache.Entry e)
    {
        if (e.Title == null && e.Artist == null && e.Album == null &&
            e.AlbumArt == null && e.Mbid == null) return null;
        return t with
        {
            Album = !string.IsNullOrEmpty(t.Album) ? t.Album : e.Album,
            Artist = !string.IsNullOrEmpty(t.Artist) ? t.Artist : e.Artist ?? "",
            AlbumArt = t.AlbumArt ?? e.AlbumArt,
            ExternalId = string.IsNullOrEmpty(t.ExternalId) ? e.Mbid : t.ExternalId,
        };
    }

    private async Task<SpotifyClient> GetClientAsync(CancellationToken ct)
    {
        if (_client != null) return _client;
        await _initGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (_client != null) return _client;
            var config = SpotifyClientConfig
                .CreateDefault()
                .WithAuthenticator(new ClientCredentialsAuthenticator(_clientId, _clientSecret));
            _client = new SpotifyClient(config);
            return _client;
        }
        finally
        {
            _initGate.Release();
        }
    }

    private async Task<MetadataCache.Entry?> EnrichByUriAsync(string spotifyUri, CancellationToken ct)
    {
        try
        {
            var id = spotifyUri.Substring("spotify:track:".Length);
            var client = await GetClientAsync(ct).ConfigureAwait(false);
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

    private async Task<MetadataCache.Entry?> EnrichByTextAsync(string artist, string title,
                                                               CancellationToken ct)
    {
        try
        {
            var client = await GetClientAsync(ct).ConfigureAwait(false);

            // Strip leading "The " — "The Beatles" search returns better
            // hits than artist:"The Beatles" in MB-normalized corpora.
            var qArtist = artist.StartsWith("The ", StringComparison.OrdinalIgnoreCase)
                ? artist.Substring(4) : artist;
            var query = $"track:\"{Escape(title)}\" artist:\"{Escape(qArtist)}\"";
            var resp = await client.Search.Item(
                new SearchRequest(SearchRequest.Types.Track, query) { Limit = 1 }, ct)
                .ConfigureAwait(false);

            var first = resp.Tracks?.Items?.FirstOrDefault();
            if (first == null) return null;
            return await BuildEntry(first, ct).ConfigureAwait(false);
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
            {
                try { art = await _httpForArt.GetByteArrayAsync(pick.Url, ct).ConfigureAwait(false); }
                catch (Exception ex) { Log($"art fetch failed: {ex.Message}"); }
            }
        }

        return new MetadataCache.Entry(
            Title: t.Name,
            Artist: artistName,
            Album: albumName,
            AlbumArt: art,
            Mbid: t.Uri);
    }

    private static string Escape(string s)
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
        _httpForArt.Dispose();
        _initGate.Dispose();
        return ValueTask.CompletedTask;
    }
}
