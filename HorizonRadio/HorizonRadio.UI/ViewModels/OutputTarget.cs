namespace HorizonRadio.UI.ViewModels;

/// <summary>
/// One entry in the Now Playing "Output" picker. Either the active game (the
/// default — audio plays through the game's radio) or a local render device for
/// testing playback without launching the game.
/// </summary>
public sealed record OutputTarget(bool IsBridge, string? DeviceId, string Name)
{
    /// <summary>The default destination: the active game, by name.</summary>
    public static OutputTarget Game(string gameName) => new(true, null, gameName);
}
