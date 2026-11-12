namespace HorizonRadio.Source.Spotify;

/// <summary>
/// librespot couldn't play a track and gave up on it. <see cref="AudioKeyRefused"/>: Spotify
/// refused the key that decrypts it, which since late 2025 it does for more and more accounts
/// (librespot #1649), so it isn't a problem with the track itself. Otherwise the track isn't
/// playable here (region, licensing).
/// </summary>
public sealed class SpotifyTrackUnavailableException : Exception
{
    public SpotifyTrackUnavailableException(string trackUri, bool audioKeyRefused)
        : base(audioKeyRefused
            ? $"Spotify refused to give librespot the audio for {trackUri} (audio key error). "
              + "Spotify blocks librespot for some accounts; the track itself is fine."
            : $"Spotify says {trackUri} isn't available to play.")
    {
        TrackUri = trackUri;
        AudioKeyRefused = audioKeyRefused;
    }

    public SpotifyTrackUnavailableException() : this("", false) { }

    public SpotifyTrackUnavailableException(string message) : base(message) { TrackUri = ""; }

    public SpotifyTrackUnavailableException(string message, Exception inner) : base(message, inner) { TrackUri = ""; }

    public string TrackUri { get; }

    public bool AudioKeyRefused { get; }
}
