using HorizonRadio.Sdk;
using HorizonRadio.Sdk.Sources;
using HorizonRadio.Sdk.Sources.Config;
using HorizonRadio.Sdk.Tools;
using HorizonRadio.Tools.YtDlp;

namespace HorizonRadio.Source.YouTube;

/// <summary>
/// Factory for the YouTube source. Exposes yt-dlp + ffmpeg paths (auto-detected
/// if bundled or on PATH) and the URL to play; <see cref="YouTubeContentPlayer"/>
/// expands the URL into items for the queue.
/// </summary>
public sealed class YouTubeSourceFactory : IContentSourceFactory, ISearchSource, IReferenceAudioSource
{
    /// <summary>Catalog id for the YouTube source — the key search results carry so the
    /// enqueuer can find this factory again.</summary>
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
    public int SortOrder => 30;
    public string DisplayName => "YouTube";
    public string? Description => "Stream audio from a YouTube video or playlist URL via yt-dlp.";

    public IReadOnlyList<ConfigField> Schema { get; }

    private readonly ISourceSettings _settings;
    private readonly IToolLocator _tools;
    private readonly YtDlpClient _ytDlp;
    private readonly IProcesses _processes;
    private readonly IPluginLog _log;
    private readonly IAudioCaches _caches;

    public YouTubeSourceFactory(ISourceSettings settings, IToolLocator tools, YtDlpClient ytDlp,
        IProcesses processes, IPluginLog log, IAudioCaches caches)
    {
        _settings = settings;
        _tools = tools;
        _ytDlp = ytDlp;
        _processes = processes;
        _log = log;
        _caches = caches;

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
        var tools = FindTools(values) ?? throw new InvalidOperationException(
            _tools.Find(ToolKind.YtDlp, values.GetString(KeyYtDlp)) is null
                ? "YouTube: pick a yt-dlp.exe path."
                : "YouTube: pick an ffmpeg.exe path.");

        AudioCache? cache = null;
        if (values.GetBool(KeyDownloadAhead, true))
        {
            cache = _caches.ForSource(SourceId);
            cache.MaxBytes = KeepSizes.GetValueOrDefault(values.GetString(KeyKeepDownloads) ?? "", 1L << 30);
        }
        return new YouTubeContentPlayer(tools, values.GetBool(KeyNormalise, false), cache);
    }

    // The tools to run: the paths set in this source's settings, else the copies installed from
    // the Tools tab (or shipped with the app). Null when either is missing.
    private YouTubeTools? FindTools(ConfigValues values) =>
        _tools.Find(ToolKind.YtDlp, values.GetString(KeyYtDlp)) is { } ytDlp
        && _tools.Find(ToolKind.Ffmpeg, values.GetString(KeyFfmpeg)) is { } ffmpeg
            ? new YouTubeTools(_ytDlp, ytDlp, ffmpeg, _processes, _log)
            : null;

    // -- ISearchSource (yt-dlp ytsearch → youtube.com/watch locators) --

    public Task<IReadOnlyList<SearchResult>> SearchAsync(string query, int limit, CancellationToken ct = default)
    {
        // Read fresh, so installing yt-dlp or changing its path applies at once. Not installed, or
        // an empty query → no results (never throw, so a missing tool can't break a search that
        // spans other sources).
        var ytDlp = _tools.Find(ToolKind.YtDlp, _settings.Load(this).GetString(KeyYtDlp));
        if (ytDlp is null || string.IsNullOrWhiteSpace(query))
            return Task.FromResult<IReadOnlyList<SearchResult>>([]);

        return YouTubeSearch.SearchTracksAsync(_ytDlp, _log, ytDlp, query.Trim(), limit, ct);
    }

    // -- IReferenceAudioSource (yt-dlp download → ffmpeg decode, as fast as it downloads) --

    /// <summary>Any web page: yt-dlp knows most video and audio sites.</summary>
    public bool CanFetch(string locator) =>
        Uri.TryCreate(locator, UriKind.Absolute, out var uri) && uri.Scheme is "http" or "https";

    public Task<ReferenceAudio?> FetchAsync(string locator, int sampleRate, TimeSpan maxLength, CancellationToken ct)
    {
        if (!CanFetch(locator) || FindTools(_settings.Load(this)) is not { } tools)
            return Task.FromResult<ReferenceAudio?>(null);
        return YouTubeReferenceAudio.FetchAsync(tools, locator, sampleRate, maxLength, ct);
    }
}
