using HorizonRadio.Core.Events;
using HorizonRadio.Core.Models;
using HorizonRadio.Core.Sources;

namespace HorizonRadio.Core.Games;

/// <summary>
/// The running connection to one game, built by an <c>IGamePlugin</c>. The host drives
/// everything game-facing through this: where audio goes, which game events exist, the
/// connection state, and the optional capabilities below. A capability a game doesn't support is
/// <see langword="null"/>, and the host hides the matching UI.
/// </summary>
public interface IGameIntegration : IAsyncDisposable
{
    /// <summary>User-facing game name (e.g. "Forza Horizon 6") for labels like the output picker
    /// and the connection indicator.</summary>
    string DisplayName { get; }

    /// <summary>The game's audio input — the primary destination for the playback pipeline — or
    /// <see langword="null"/> when the integration plays nothing in-game (audio then goes to local
    /// speakers only).</summary>
    IPcmSink? Output { get; }

    /// <summary>Producers of <see cref="GameEvent"/>s (memory pollers, telemetry, …). Fixed for the
    /// integration's lifetime; may be empty.</summary>
    IReadOnlyList<IGameEventSource> EventSources { get; }

    /// <summary>Current link to the game.</summary>
    ConnectionState State { get; }

    /// <summary>Raised when <see cref="State"/> changes. May fire on a background thread. On a
    /// transition to <see cref="ConnectionState.Connected"/> the host re-sends its state (target
    /// station, volume), since the game side may have restarted.</summary>
    event Action<ConnectionState>? StateChanged;

    /// <summary>Start connecting (and any listeners). Called once, after the host has subscribed
    /// to <see cref="StateChanged"/> and the event sources.</summary>
    void Start();

    /// <summary>Choose which in-game station to replace, or <see langword="null"/>.</summary>
    IStationTargeting? Stations { get; }

    /// <summary>In-game volume control, or <see langword="null"/>.</summary>
    IGameVolume? Volume { get; }

    /// <summary>Show the current track inside the game, or <see langword="null"/>.</summary>
    INowPlayingDisplay? NowPlaying { get; }
}

/// <summary>A game with several radio stations, where the user picks the one we replace.</summary>
public interface IStationTargeting
{
    /// <summary>The game's station names, exactly as the game reports them.</summary>
    IReadOnlyList<string> Stations { get; }

    /// <summary>Replace <paramref name="station"/> (one of <see cref="Stations"/>), or whatever
    /// station is active when <see langword="null"/>. Best-effort: returns false when the game
    /// isn't connected; the host re-sends on reconnect.</summary>
    bool SetTarget(string? station);
}

/// <summary>Volume applied to our audio inside the game.</summary>
public interface IGameVolume
{
    /// <summary>The user's master volume as a linear gain (0..1). Best-effort; false when not
    /// connected.</summary>
    bool SetMasterVolume(float gain);

    /// <summary>A temporary duck gain (0..1) applied on top of the master volume — the Events /
    /// Controls "set volume" action. Best-effort; false when not connected.</summary>
    bool SetDuckGain(float gain);
}

/// <summary>Writes the current track into the game's own now-playing display.</summary>
public interface INowPlayingDisplay
{
    /// <summary>Show <paramref name="track"/>. Best-effort; false when not connected (the next
    /// track change retries).</summary>
    bool Show(Track track);
}
