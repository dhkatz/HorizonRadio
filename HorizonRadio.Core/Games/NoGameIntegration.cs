using HorizonRadio.Core.Events;
using HorizonRadio.Core.Models;
using HorizonRadio.Core.Sources;

namespace HorizonRadio.Core.Games;

/// <summary>
/// The "no game" choice: Horizon Radio as a plain music player. It has no game audio output (the
/// app plays through local speakers), reports no events, adds no pages, and never connects, so the
/// shell shows no game section, chip, or banner.
/// </summary>
public sealed class NoGameIntegration : IGameIntegration
{
    /// <summary>The persisted game id meaning "no game".</summary>
    public const string Id = "none";

    public string DisplayName => "No Game";

    public IPcmSink? Output => null;

    public IReadOnlyList<IGameEventSource> EventSources => [];

    public IReadOnlyList<GameEventInfo> EventCatalog => [];

    public ConnectionState State => ConnectionState.Disconnected;

    public event Action<ConnectionState>? StateChanged
    {
        add { }
        remove { }
    }

    public void Start(IGameHost host) { }

    public IStationTargeting? Stations => null;

    public IGameVolume? Volume => null;

    public INowPlayingDisplay? NowPlaying => null;

    public IReadOnlyList<GamePage> Pages => [];

    public GameNotice? Notice => null;

    public event Action? NoticeChanged
    {
        add { }
        remove { }
    }

    public string? EventsHint => null;

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}
