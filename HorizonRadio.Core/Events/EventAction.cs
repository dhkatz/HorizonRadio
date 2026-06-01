namespace HorizonRadio.Core.Events;

/// <summary>What an event rule does when its event fires.</summary>
public enum EventActionType
{
    None = 0,
    NextTrack,
    PreviousTrack,
    RestartTrack,
    Pause,
    Resume,
    SwitchSource, // Param = source id (e.g. "local")
    SetVolume,    // Param = level 0..1 as invariant string (e.g. "0.3" to duck)
}

/// <summary>
/// A configured action. <see cref="Param"/> carries the target for the
/// action types that need one (source id, volume level).
/// </summary>
public sealed record EventAction(EventActionType Type, string? Param = null)
{
    public static readonly EventAction None = new(EventActionType.None);
}
