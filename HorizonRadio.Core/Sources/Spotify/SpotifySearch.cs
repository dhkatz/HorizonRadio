using SpotifyAPI.Web;

namespace HorizonRadio.Core.Sources.Spotify;

/// <summary>
/// Maps the Spotify Web API Search endpoint to <see cref="SearchResult"/>s. Kept
/// here (not in the factory or the UI) so the SpotifyAPI.Web specifics — the request
/// shape, the FullTrack field layout, album-art sizing — stay encapsulated in the
/// Spotify source, the same way librespot/yt-dlp details do for their sources.
///
/// Verified working under a bring-your-own Development-Mode app (free-text Search
/// and the <c>isrc:</c> filter both return 200), so we map results straight to
/// <c>spotify:track:…</c> locators — no ISRC cross-reference needed.
/// </summary>
internal static class SpotifySearch
{
    // Serialize search calls: rapid typing (plus the token refresh the PKCE
    // authenticator may do mid-request) means overlapping calls can race the shared
    // client. One at a time keeps that safe; debounce already keeps the count low.
    private static readonly SemaphoreSlim Gate = new(1, 1);

    // v1 returns tracks only. Albums/playlists slot in later behind SearchResultKind
    // (and enqueue the same way, since the queue resolver already enumerates them).
    private static void Log(string msg) => Diagnostics.ProcessConsole.Append("search", msg);

    public static async Task<IReadOnlyList<SearchResult>> SearchTracksAsync(
        SpotifyConnection connection, string query, int limit, CancellationToken ct)
    {
        var client = await connection.GetClientAsync(ct).ConfigureAwait(false);
        if (client is null)
        {
            // Empty-but-no-error here is what reads as a false "no results": the token
            // refresh failed or the account isn't connected. Surface it so it's visible.
            Log($"'{query}': no Spotify client — reconnect Spotify (token refresh failed or not connected).");
            return [];
        }

        var request = new SearchRequest(SearchRequest.Types.Track, query)
        {
            // Docs say max 50, but a Development-Mode app caps the search limit at 10
            // (default 5) and returns "Invalid limit" above that — same Dev-Mode
            // tightening as the removed endpoints/fields. Clamp so no caller can trip it.
            Limit = Math.Clamp(limit, 1, 10),
        };

        await Gate.WaitAsync(ct).ConfigureAwait(false);
        SearchResponse response;
        try { response = await client.Search.Item(request, ct).ConfigureAwait(false); }
        finally { Gate.Release(); }

        var tracks = response.Tracks?.Items;
        Log($"'{query}' → {tracks?.Count ?? 0} track(s)");
        if (tracks is null || tracks.Count == 0) return [];

        var results = new List<SearchResult>(tracks.Count);
        foreach (var t in tracks)
        {
            results.Add(new SearchResult(
                SourceId: SpotifyContentSourceFactory.SourceId,
                Kind: SearchResultKind.Track,
                Title: t.Name,
                Subtitle: string.Join(", ", t.Artists.Select(a => a.Name)),
                ArtUrl: SmallestImageUrl(t.Album?.Images),
                Locator: t.Uri));
        }
        return results;
    }

    // Spotify orders album images largest-first; the last is the smallest, which is
    // ample for a result-row thumbnail and the cheapest to fetch and decode.
    private static string? SmallestImageUrl(IList<Image>? images)
        => images is { Count: > 0 } ? images[^1].Url : null;
}
