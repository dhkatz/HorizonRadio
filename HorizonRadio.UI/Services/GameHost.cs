using HorizonRadio.Core.Games;

namespace HorizonRadio.UI.Services;

/// <summary>The host services handed to the active game integration when it starts.</summary>
internal sealed class GameHost(IPlaybackRouter playback) : IGameHost
{
    public IPlaybackRouter Playback { get; } = playback;
}
