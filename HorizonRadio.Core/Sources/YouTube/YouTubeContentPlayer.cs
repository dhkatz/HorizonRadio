namespace HorizonRadio.Core.Sources.YouTube;

/// <summary>
/// Content-free YouTube engine: holds the yt-dlp/ffmpeg paths and the
/// normalization behavior, and opens a <see cref="YouTubeSource"/> for a given
/// <see cref="ContentRef"/> whose locator is a video or playlist URL. Keeps the
/// yt-dlp/ffmpeg specifics encapsulated here rather than leaking them up to the
/// mix engine — the engine only ever hands us a URL.
/// </summary>
public sealed class YouTubeContentPlayer(string ytDlpPath, string ffmpegPath, bool normalise) : IContentPlayer
{
    public IAudioSource Open(ContentRef content)
    {
        if (string.IsNullOrWhiteSpace(content.Locator))
            throw new InvalidOperationException("YouTube: enter a video or playlist URL.");

        return new YouTubeSource(new YouTubeOptions
        {
            YtDlpPath = ytDlpPath,
            FfmpegPath = ffmpegPath,
            Url = content.Locator,
            EnableVolumeNormalisation = normalise,
        });
    }
}
