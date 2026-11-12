using HorizonRadio.Sdk.Sources;

namespace HorizonRadio.Source.YouTube;

/// <summary>
/// Content-free YouTube engine: holds the tools it runs and the
/// normalization behavior, and expands a <see cref="ContentRef"/> whose locator is
/// a video or playlist URL into <see cref="YouTubePlayableItem"/>s. Keeps the
/// yt-dlp/ffmpeg specifics encapsulated here rather than leaking them up to the
/// queue — the queue only ever hands us a URL. Its videos download ahead into
/// <paramref name="cache"/> (null: they only stream).
/// </summary>
public sealed class YouTubeContentPlayer(YouTubeTools tools, bool normalise, AudioCache? cache = null)
    : IContentPlayer, IContentDescriber
{
    public async Task<IReadOnlyList<PlayableItem>> EnumerateAsync(ContentRef content, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(content.Locator))
            throw new InvalidOperationException("YouTube: enter a video or playlist URL.");

        // --flat-playlist enumerate: cheap id/title list now; each item does its
        // own (short-lived, signed-URL) resolve lazily in PrepareAsync/PlayAsync.
        var entries = await tools.YtDlp.EnumerateAsync(tools.YtDlpPath, content.Locator, ct).ConfigureAwait(false);
        return [.. entries.Select(e => (PlayableItem)new YouTubePlayableItem(e, tools, normalise, cache))];
    }

    /// <summary>A playlist's title, owner, size, and thumbnail. A single video is left to the
    /// host (null), and isn't even listed: telling them apart needs only the link.</summary>
    public async Task<ContentSummary?> DescribeAsync(ContentRef content, CancellationToken ct)
    {
        if (!YouTubeLinks.IsCollection(content.Locator)) return null;

        var listing = await tools.YtDlp.ListAsync(tools.YtDlpPath, content.Locator, ct).ConfigureAwait(false);
        if (!listing.IsPlaylist) return null;
        return new ContentSummary(
            Title: string.IsNullOrWhiteSpace(listing.Title) ? "YouTube playlist" : listing.Title,
            Subtitle: listing.Uploader,
            Kind: ContentKind.Playlist,
            ItemCount: listing.Entries.Count,
            ArtUrl: listing.ThumbnailUrl);
    }
}
