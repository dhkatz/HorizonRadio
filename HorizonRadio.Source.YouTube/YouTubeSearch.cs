namespace HorizonRadio.Core.Sources.YouTube;

/// <summary>
/// Maps yt-dlp search hits to <see cref="SearchResult"/>s. Kept here (not in the
/// factory or the UI) so the yt-dlp specifics — the <c>ytsearch</c> pseudo-URL, the
/// flat-entry field layout — stay encapsulated in the YouTube source, the same way
/// the Spotify Web API details do in <see cref="Spotify.SpotifySearch"/>.
///
/// A hit's canonical watch URL is the locator: the existing YouTube content player
/// already resolves and plays it, so "search → queue" needs no new playback code.
/// </summary>
internal static class YouTubeSearch
{
    private static void Log(string msg) => Diagnostics.ProcessConsole.Append("search", msg);

    public static async Task<IReadOnlyList<SearchResult>> SearchTracksAsync(
        string ytDlpPath, string query, int limit, CancellationToken ct)
    {
        var hits = await YtDlpClient.SearchAsync(ytDlpPath, query, limit, ct).ConfigureAwait(false);
        Log($"'{query}' → {hits.Count} YouTube result(s)");
        if (hits.Count == 0) return [];

        var results = new List<SearchResult>(hits.Count);
        foreach (var h in hits)
        {
            results.Add(new SearchResult(
                SourceId: YouTubeSourceFactory.SourceId,
                Kind: SearchResultKind.Track,
                Title: h.Title,
                Subtitle: h.Uploader,
                ArtUrl: h.ThumbnailUrl,
                Locator: h.WebpageUrl,
                Duration: h.Duration));
        }
        return results;
    }
}
