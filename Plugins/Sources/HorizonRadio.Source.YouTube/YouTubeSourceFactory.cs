using HorizonRadio.Core.Sources;
using HorizonRadio.Core.Tools;
using HorizonRadio.Sdk.Sources;
using HorizonRadio.Sdk.Sources.Config;
using HorizonRadio.Sdk.Tools;

namespace HorizonRadio.Source.YouTube;

/// <summary>
/// Factory for the YouTube source. Exposes yt-dlp + ffmpeg paths (auto-detected
/// if bundled or on PATH) and the URL to play; <see cref="YouTubeContentPlayer"/>
/// expands the URL into items for the queue.
/// </summary>
public sealed class YouTubeSourceFactory : IContentSourceFactory, ISearchSource, IReferenceAudioSource
{
    /// <summary>Catalog id for the YouTube source — the key search results carry so the
    /// enqueuer can find this factory again (see <see cref="SourceCatalog.Find"/>).</summary>
    public const string SourceId = "youtube";

    public const string KeyYtDlp = "ytDlp";
    public const string KeyFfmpeg = "ffmpeg";
    public const string KeyUrl = "url";
    public const string KeyNormalise = "normalise";
    public const string KeyDownloadAhead = "downloadAhead";
    public const string KeyKeepDownloads = "keepDownloads";

    /// <summary>How much downloaded audio to keep, by the label the settings show.</summary>
    private static readonly IReadOnlyDictionary<string, long> KeepSizes = new Dictionary<string, long>
    {
        ["250 MB"] = 250L << 20,
        ["1 GB"] = 1L << 30,
        ["4 GB"] = 4L << 30,
        ["10 GB"] = 10L << 30,
    };

    public string Id => SourceId;
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
                ToolKind:    ToolKind.YtDlp,
                Description: "Install via the Tools tab, or point at an existing yt-dlp.exe."),

            new ToolField(
                Key:         KeyFfmpeg,
                Label:       "ffmpeg.exe",
                ToolKind:    ToolKind.Ffmpeg,
                Description: "Install via the Tools tab, or point at an existing ffmpeg.exe."),

            new BoolField(
                Key:         KeyNormalise,
                Label:       "Volume normalisation",
                Default:     false,
                Description: "Apply EBU R128 loudnorm. Costs a little CPU; off by default."),

            new BoolField(
                Key:         KeyDownloadAhead,
                Label:       "Download songs ahead",
                Default:     true,
                Description: "Download the next song while one plays, so it starts at once, can be skipped " +
                             "through, and plays again without the network. Off: songs stream as they play."),

            new EnumField(
                Key:         KeyKeepDownloads,
                Label:       "Keep downloaded songs",
                Options:     [.. KeepSizes.Keys],
                Default:     "1 GB",
                Description: "Past this, the songs played longest ago are removed."),
        ];
    }

    /// <summary>The URL field is the content locator; everything else
    /// (tool paths, normalization) is environment/behavior the player holds.</summary>
    public string ContentKey => KeyUrl;

    public string LocatorHint => "https://youtube.com/watch?v=… or /playlist?list=…";

    public bool Recognizes(string locator) => YouTubeLinks.IsYouTube(locator);

    public IContentPlayer CreatePlayer(ConfigValues values)
    {
        // Prefer the per-source path, but fall back to a managed/bundled copy so a tool
        // installed once (Tools tab) works without re-entering it here.
        var ytDlp = ToolResolver.Resolve(values.GetString(KeyYtDlp), ToolKind.YtDlp);
        if (ytDlp is null)
            throw new InvalidOperationException("YouTube: pick a yt-dlp.exe path.");

        var ffmpeg = ToolResolver.Resolve(values.GetString(KeyFfmpeg), ToolKind.Ffmpeg);
        if (ffmpeg is null)
            throw new InvalidOperationException("YouTube: pick an ffmpeg.exe path.");

        AudioCache? cache = null;
        if (values.GetBool(KeyDownloadAhead, true))
        {
            cache = AudioCache.For(SourceId);
            cache.MaxBytes = KeepSizes.GetValueOrDefault(values.GetString(KeyKeepDownloads) ?? "", 1L << 30);
        }
        return new YouTubeContentPlayer(ytDlp, ffmpeg, values.GetBool(KeyNormalise, false), cache);
    }

    // -- ISearchSource (yt-dlp ytsearch → youtube.com/watch locators) --

    public Task<IReadOnlyList<SearchResult>> SearchAsync(string query, int limit, CancellationToken ct = default)
    {
        var ytDlp = YouTubeRuntime.YtDlpPath;
        // yt-dlp not installed/configured, or empty query → no results (never throw, so a
        // missing tool can't break a search that spans other sources).
        if (ytDlp is null || string.IsNullOrWhiteSpace(query))
            return Task.FromResult<IReadOnlyList<SearchResult>>([]);

        return YouTubeSearch.SearchTracksAsync(ytDlp, query.Trim(), limit, ct);
    }

    // -- IReferenceAudioSource (yt-dlp download → ffmpeg decode, as fast as it downloads) --

    /// <summary>Any web page: yt-dlp knows most video and audio sites.</summary>
    public bool CanFetch(string locator) =>
        Uri.TryCreate(locator, UriKind.Absolute, out var uri) && uri.Scheme is "http" or "https";

    public Task<ReferenceAudio?> FetchAsync(string locator, int sampleRate, TimeSpan maxLength, CancellationToken ct)
    {
        var ytDlp = ToolResolver.Resolve(YouTubeRuntime.YtDlpPath, ToolKind.YtDlp);
        var ffmpeg = ToolResolver.Resolve(YouTubeRuntime.FfmpegPath, ToolKind.Ffmpeg);
        if (ytDlp is null || ffmpeg is null || !CanFetch(locator)) return Task.FromResult<ReferenceAudio?>(null);
        return YouTubeReferenceAudio.FetchAsync(ytDlp, ffmpeg, locator, sampleRate, maxLength, ct);
    }
}
