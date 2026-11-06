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
    /// transition to <see cref="ConnectionState.Connected"/> the host re-sends its own state
    /// (volume), since the game side may have restarted; the game re-asserts its own.</summary>
    event Action<ConnectionState>? StateChanged;

    /// <summary>Start connecting (and any listeners). Called once, after the host has subscribed
    /// to <see cref="StateChanged"/> and the event sources. <paramref name="host"/> lets the game
    /// steer playback (e.g. as the player tunes its radio).</summary>
    void Start(IGameHost host);

    /// <summary>The game's radio stations and which is tuned in, or <see langword="null"/> for a
    /// game without stations. Which station plays what is the game's own setting.</summary>
    IStationTargeting? Stations { get; }

    /// <summary>In-game volume control, or <see langword="null"/>.</summary>
    IGameVolume? Volume { get; }

    /// <summary>Show the current track inside the game, or <see langword="null"/>.</summary>
    INowPlayingDisplay? NowPlaying { get; }

    /// <summary>Pages this game adds to the app, listed in the sidebar under the game's name (e.g.
    /// a mod installer, bridge diagnostics). Built on first access, on the UI thread; may be
    /// empty.</summary>
    IReadOnlyList<GamePage> Pages { get; }

    /// <summary>A setup problem the user should act on (e.g. the in-game mod is missing), shown as
    /// a banner on every page, or <see langword="null"/> when all is well. Only meaningful once
    /// <see cref="Pages"/> has been built (pages usually own the state it describes).</summary>
    GameNotice? Notice { get; }

    /// <summary>Raised when <see cref="Notice"/> changes. Raised on the UI thread.</summary>
    event Action? NoticeChanged;

    /// <summary>One line of setup advice for the Events page (e.g. how to enable the game's
    /// telemetry feed), or <see langword="null"/>.</summary>
    string? EventsHint { get; }
}

/// <summary>
/// A page a game integration contributes. The host renders <see cref="ViewModel"/> by convention:
/// the view is the type in the view model's assembly whose full name is the view model's with
/// "ViewModel" replaced by "View" (e.g. <c>Foo.ViewModels.StatsViewModel</c> →
/// <c>Foo.Views.StatsView</c>), so the SDK stays UI-framework-free.
/// </summary>
/// <param name="Id">Stable, lowercase id, unique within the game.</param>
/// <param name="Title">Sidebar label.</param>
/// <param name="Icon">Material Design icon name (e.g. <c>PuzzleOutline</c>); unknown names fall
/// back to a generic icon.</param>
/// <param name="ViewModel">The page's view model.</param>
public sealed record GamePage(string Id, string Title, string Icon, object ViewModel);

/// <summary>A setup problem surfaced as a banner, with a shortcut to the page that fixes it.</summary>
/// <param name="Title">Short headline.</param>
/// <param name="Message">One-sentence explanation.</param>
/// <param name="PageId">The <see cref="GamePage.Id"/> the banner's button opens.</param>
public sealed record GameNotice(string Title, string Message, string PageId);

/// <summary>
/// A game with radio stations. The game decides what each station plays (its own music, the app's
/// player, or a mix) and routes playback as the player tunes; the host only reads this to show
/// where the player is.
/// </summary>
public interface IStationTargeting
{
    /// <summary>The game's station names, exactly as the game reports them.</summary>
    IReadOnlyList<string> Stations { get; }

    /// <summary>The station tuned in right now, or <see langword="null"/> when unknown (e.g. the
    /// game isn't connected).</summary>
    string? CurrentStation { get; }

    /// <summary>Raised when <see cref="CurrentStation"/> or a station's assignment changes. May
    /// fire on any thread.</summary>
    event Action? Changed;
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
