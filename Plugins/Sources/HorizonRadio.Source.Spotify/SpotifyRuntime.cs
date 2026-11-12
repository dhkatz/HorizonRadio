using HorizonRadio.Core.Diagnostics;

namespace HorizonRadio.Source.Spotify;

/// <summary>
/// App-level Spotify singletons, set once at startup so the parameterless
/// <see cref="SpotifyContentSourceFactory"/> (constructed inside the static
/// <see cref="SourceCatalog"/>) can reach the authenticated connection and the
/// shared librespot playback service. Deliberately static, mirroring
/// <see cref="ProcessConsole"/>: the catalog has no DI seam, and the
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

    /// <summary>Plays the tracks Spotify refuses librespot (when the user has set Soloist up).</summary>
    public static SoloistPlaybackService? Soloist { get; private set; }

    /// <summary>Whether tracks go to Soloist first, kept across sessions.</summary>
    public static SpotifyPlayerChoice? PlayerChoice { get; private set; }

    public static void Initialize(SpotifyConnection connection, SpotifyPlaybackService playback,
                                  SoloistPlaybackService? soloist = null, SpotifyPlayerChoice? playerChoice = null)
    {
        Connection = connection;
        Playback = playback;
        Soloist = soloist;
        PlayerChoice = playerChoice ?? new SpotifyPlayerChoice(SpotifyPlayerChoice.DefaultPath);
    }
}
