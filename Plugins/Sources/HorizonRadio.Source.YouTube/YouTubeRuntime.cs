namespace HorizonRadio.Core.Sources.YouTube;

/// <summary>
/// App-level YouTube wiring, set once at startup so the parameterless
/// <see cref="YouTubeSourceFactory"/> (constructed inside the static
/// <see cref="SourceCatalog"/>) can reach the configured yt-dlp path when it searches.
/// Mirrors <see cref="Spotify.SpotifyRuntime"/>: the catalog has no DI seam, and
/// threading config through every factory call buys nothing.
///
/// Holds a resolver (not a captured path) so it reads the path fresh each time —
/// installing yt-dlp via the Tools tab mid-session then takes effect without a restart.
/// The resolver returns null when yt-dlp isn't configured/installed yet, which the
/// search source treats as "no results" rather than an error.
/// </summary>
public static class YouTubeRuntime
{
    private static Func<string?>? _ytDlpPath;

    public static void Initialize(Func<string?> ytDlpPath) => _ytDlpPath = ytDlpPath;

    /// <summary>The currently-configured yt-dlp.exe path, or null when unset/missing
    /// (or before <see cref="Initialize"/> runs — e.g. headless tools, the designer).</summary>
    public static string? YtDlpPath => _ytDlpPath?.Invoke();
}
