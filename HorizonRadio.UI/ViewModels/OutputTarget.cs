namespace HorizonRadio.UI.ViewModels;

/// <summary>
/// One entry in the Now Playing "Output" picker. Either the in-game bridge
/// (the default — audio plays through the game's radio) or a local render
/// device for testing playback without launching the game.
/// </summary>
public sealed record OutputTarget(bool IsBridge, string? DeviceId, string Name)
{
    /// <summary>The default destination: the FH6 audio bridge.</summary>
    public static OutputTarget Bridge { get; } = new(true, null, "Forza Horizon 6");
}
