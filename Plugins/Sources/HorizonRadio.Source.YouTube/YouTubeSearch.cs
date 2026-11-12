using HorizonRadio.Sdk;
using HorizonRadio.Sdk.Sources;
using HorizonRadio.Tools.YtDlp;

namespace HorizonRadio.Source.YouTube;

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
    public static async Task<IReadOnlyList<SearchResult>> SearchTracksAsync(
        YtDlpClient client, IPluginLog log, string ytDlpPath, string query, int limit, CancellationToken ct)
    {
        var hits = await client.SearchAsync(ytDlpPath, query, limit, ct).ConfigureAwait(false);
        log.Write("search", $"'{query}' → {hits.Count} YouTube result(s)");
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
