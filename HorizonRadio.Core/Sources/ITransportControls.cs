using System;
using System.Threading.Tasks;

namespace HorizonRadio.Core.Sources;

/// <summary>
/// Optional capability on an <see cref="IAudioSource"/>: per-track
/// transport (play/pause/next/prev). Sources implement this when they
/// can meaningfully respond to it — local files yes (just walk the
/// playlist), Spotify no (transport is owned by the user's Spotify
/// app via Spotify Connect; librespot doesn't accept commands from
/// outside on the version we ship).
///
/// The UI checks <c>activeSource is ITransportControls tc</c> and
/// enables/disables buttons accordingly. <see cref="CanX"/> properties
/// let sources further narrow it down at runtime (e.g. a one-element
/// playlist disabling Next/Previous).
/// </summary>
public interface ITransportControls
{
    bool CanPause { get; }
    bool CanSkipNext { get; }
    bool CanSkipPrevious { get; }

    bool IsPaused { get; }

    Task TogglePauseAsync();
    Task NextAsync();
    Task PreviousAsync();

    /// <summary>Restart the current track from the beginning. Default is a
    /// no-op so sources that can't seek (or don't want to) need not opt in;
    /// playlist sources override it to replay the current entry.</summary>
    Task RestartAsync() => Task.CompletedTask;

    /// <summary>Whether this source can play in a shuffled order — typically
    /// true only for a multi-track playlist. Defaults false so non-playlist
    /// sources need not opt in.</summary>
    bool CanShuffle => false;

    /// <summary>Whether shuffle is currently on. Defaults false.</summary>
    bool IsShuffled => false;

    /// <summary>Turn shuffle on or off live. Toggling mid-playback keeps the
    /// current track playing and shuffles the remaining order around it.
    /// Default is a no-op for sources that don't support it.</summary>
    Task SetShuffleAsync(bool enabled) => Task.CompletedTask;

    /// <summary>Raised when pause state changes (either via
    /// TogglePauseAsync or by the source itself, e.g. EOF stall).</summary>
    event Action<bool>? PausedChanged;
}
