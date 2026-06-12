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
    TogglePause,  // play/pause on one binding — the natural hotkey shape
    SwitchSource, // Param = source id (e.g. "spotify") — a self-driven source/mode
    SwitchMix,    // Param = mix id — switch to a specific saved mix
    NextMix,      // cycle to the next saved mix
    PreviousMix,  // cycle to the previous saved mix
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
