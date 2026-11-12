using HorizonRadio.Sdk.Games;

namespace HorizonRadio.Core.Games;

/// <summary>The host services handed to the active game integration when it starts.</summary>
public sealed class GameHost(IPlaybackRouter playback) : IGameHost
{
    public IPlaybackRouter Playback { get; } = playback;
}
