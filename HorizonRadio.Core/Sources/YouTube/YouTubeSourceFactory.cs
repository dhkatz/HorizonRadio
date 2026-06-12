using HorizonRadio.Core.Sources.Config;
using HorizonRadio.Core.Tools;

namespace HorizonRadio.Core.Sources.YouTube;

/// <summary>
/// Factory for <see cref="YouTubeSource"/>. Exposes yt-dlp + ffmpeg
/// paths (auto-detected if bundled or on PATH) and the URL to play.
/// </summary>
public sealed class YouTubeSourceFactory : IContentSourceFactory
{
    public const string KeyYtDlp = "ytDlp";
    public const string KeyFfmpeg = "ffmpeg";
    public const string KeyUrl = "url";
    public const string KeyNormalise = "normalise";

    public string Id => "youtube";
    public string DisplayName => "YouTube";
    public string? Description => "Stream audio from a YouTube video or playlist URL via yt-dlp.";

    public IReadOnlyList<ConfigField> Schema { get; }

    public YouTubeSourceFactory()
    {
        Schema =
        [
            new TextField(
                Key:         KeyUrl,
                Label:       "Video or playlist URL",
                Placeholder: "https://www.youtube.com/watch?v=… or /playlist?list=…",
                Description: "Single video or full playlist. Playlists enable next/prev transport."),

            new ToolField(
                Key:         KeyYtDlp,
                Label:       "yt-dlp.exe",
                ToolKind:    Tools.ToolKind.YtDlp,
                Description: "Install via the Tools tab, or point at an existing yt-dlp.exe."),

            new ToolField(
                Key:         KeyFfmpeg,
                Label:       "ffmpeg.exe",
                ToolKind:    Tools.ToolKind.Ffmpeg,
                Description: "Install via the Tools tab, or point at an existing ffmpeg.exe."),

            new BoolField(
                Key:         KeyNormalise,
                Label:       "Volume normalisation",
                Default:     false,
                Description: "Apply EBU R128 loudnorm. Costs a little CPU; off by default.")
        ];
    }

    /// <summary>The URL field is the content locator; everything else
    /// (tool paths, normalization) is environment/behavior the player holds.</summary>
    public string ContentKey => KeyUrl;

    public string LocatorHint => "https://youtube.com/watch?v=… or /playlist?list=…";

    public IContentPlayer CreatePlayer(ConfigValues values)
    {
        var ytDlp = values.GetString(KeyYtDlp);
        if (string.IsNullOrWhiteSpace(ytDlp) || !File.Exists(ytDlp))
            throw new InvalidOperationException("YouTube: pick a yt-dlp.exe path.");

        var ffmpeg = values.GetString(KeyFfmpeg);
        if (string.IsNullOrWhiteSpace(ffmpeg) || !File.Exists(ffmpeg))
            throw new InvalidOperationException("YouTube: pick an ffmpeg.exe path.");

        return new YouTubeContentPlayer(ytDlp!, ffmpeg!, values.GetBool(KeyNormalise, false));
    }

    // Single-start path: build the engine, then open the one URL the form holds.
    // The content (URL) validation lives in the player's Open; the mix engine
    // reuses CreatePlayer directly and opens many refs against it.
    public IAudioSource Create(ConfigValues values)
        => CreatePlayer(values).Open(new ContentRef(Id, values.GetString(ContentKey) ?? ""));
}
