namespace HorizonRadio.Sdk.Games;

/// <summary>Host services a game integration receives when it starts.</summary>
public interface IGameHost
{
    /// <summary>Decides what the app plays as the player moves around the game (e.g. tunes the
    /// radio): the app's own player, one of the user's mixes, or nothing.</summary>
    IPlaybackRouter Playback { get; }
}

/// <summary>
/// Lets a game steer the app's playback. Each route is a channel that keeps its own place: the
/// app's player (its queue, one-offs, whatever the user started), a mix (one channel per mix), or
/// silence. Routing away from a channel parks it; routing back continues it according to the
/// route's <see cref="StationSwitchMode"/>.
/// </summary>
public interface IPlaybackRouter
{
    /// <summary>The user's mixes, for the game to offer as routes.</summary>
    IReadOnlyList<MixInfo> Mixes { get; }

    /// <summary>Raised when <see cref="Mixes"/> changes (a mix added, renamed, or deleted). May
    /// fire on any thread.</summary>
    event Action? MixesChanged;

    /// <summary>Switch playback to <paramref name="route"/>. Returns immediately; the switch
    /// happens in the background, and later calls win over earlier ones.</summary>
    void Route(PlaybackRoute route);
}

/// <summary>A mix the user saved.</summary>
public sealed record MixInfo(string Id, string Name);

/// <summary>What happens to a mix channel while it isn't the one being heard.</summary>
public enum StationSwitchMode
{
    /// <summary>It pauses, and continues where it left off on return.</summary>
    Resume,

    /// <summary>It's discarded, and starts fresh on return.</summary>
    Restart,

    /// <summary>"Live radio": time keeps passing, so on return it continues where it would be
    /// by now, and it can't be paused manually.</summary>
    Live,
}

/// <summary>Which channel the app should play.</summary>
public enum PlaybackRouteKind
{
    /// <summary>The app's own player: the queue, one-offs, whatever the user started.</summary>
    Player,

    /// <summary>One of the user's mixes (<see cref="PlaybackRoute.MixId"/>).</summary>
    Mix,

    /// <summary>Nothing: park whatever was playing (e.g. the game's own music is on).</summary>
    Silent,
}

/// <summary>A playback destination for <see cref="IPlaybackRouter.Route"/>.</summary>
public sealed record PlaybackRoute
{
    private PlaybackRoute(PlaybackRouteKind kind, string? mixId, StationSwitchMode mode)
    {
        Kind = kind;
        MixId = mixId;
        Mode = mode;
    }

    public PlaybackRouteKind Kind { get; }

    /// <summary>The mix to play, for <see cref="PlaybackRouteKind.Mix"/>.</summary>
    public string? MixId { get; }

    /// <summary>How the mix channel behaves while away (mix routes only; the player always
    /// resumes).</summary>
    public StationSwitchMode Mode { get; }

    public static PlaybackRoute Player { get; } = new(PlaybackRouteKind.Player, null, StationSwitchMode.Resume);

    public static PlaybackRoute Silent { get; } = new(PlaybackRouteKind.Silent, null, StationSwitchMode.Resume);

    public static PlaybackRoute Mix(string mixId, StationSwitchMode mode = StationSwitchMode.Resume) =>
        new(PlaybackRouteKind.Mix, mixId, mode);
}
