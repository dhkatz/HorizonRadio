using HorizonRadio.Tools.YtDlp;

namespace HorizonRadio.Core.Sources.YouTube;

/// <summary>
/// Content-free YouTube engine: holds the yt-dlp/ffmpeg paths and the
/// normalization behavior, and expands a <see cref="ContentRef"/> whose locator is
/// a video or playlist URL into <see cref="YouTubePlayableItem"/>s. Keeps the
/// yt-dlp/ffmpeg specifics encapsulated here rather than leaking them up to the
/// queue — the queue only ever hands us a URL.
/// </summary>
public sealed class YouTubeContentPlayer(string ytDlpPath, string ffmpegPath, bool normalise) : IContentPlayer
{
    public async Task<IReadOnlyList<PlayableItem>> EnumerateAsync(ContentRef content, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(content.Locator))
            throw new InvalidOperationException("YouTube: enter a video or playlist URL.");

        // --flat-playlist enumerate: cheap id/title list now; each item does its
        // own (short-lived, signed-URL) resolve lazily in PrepareAsync/PlayAsync.
        var entries = await YtDlpClient.EnumerateAsync(ytDlpPath, content.Locator, ct).ConfigureAwait(false);
        return [.. entries.Select(e => (PlayableItem)new YouTubePlayableItem(e, ytDlpPath, ffmpegPath, normalise))];
    }
}
