namespace HorizonRadio.Core.Sources.Spotify;

/// <summary>
/// App-level Spotify singletons, set once at startup so the parameterless
/// <see cref="SpotifyContentSourceFactory"/> (constructed inside the static
/// <see cref="SourceCatalog"/>) can reach the authenticated connection and the
/// shared librespot playback service. Deliberately static, mirroring
/// <see cref="Diagnostics.ProcessConsole"/>: the catalog has no DI seam, and the
/// alternative — threading these through every factory call site — buys nothing.
///
/// Null until <see cref="Initialize"/> runs (e.g. headless tools, the designer),
/// in which case the factory throws a friendly "connect Spotify first" error
/// rather than NRE'ing.
/// </summary>
public static class SpotifyRuntime
{
    public static SpotifyConnection? Connection { get; private set; }
    public static SpotifyPlaybackService? Playback { get; private set; }

    public static void Initialize(SpotifyConnection connection, SpotifyPlaybackService playback)
    {
        Connection = connection;
        Playback = playback;
    }
}
