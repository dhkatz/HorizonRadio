using System;
using System.Threading.Tasks;

namespace HorizonRadio.Core.Sources;

/// <summary>
/// Optional capability on an <see cref="IAudioSource"/>: report playback
/// position/duration, and optionally seek. Sources implement this when they
/// can meaningfully report a position — local files fully (seekable), stream
/// sources read-only (YouTube/Spotify), and not at all for endless sources
/// (the test tone). The UI <c>is</c>-checks for it (like
/// <see cref="ITransportControls"/>) and polls <see cref="Position"/>/
/// <see cref="Duration"/> to drive a progress/seek bar.
/// </summary>
public interface IPlaybackProgress
{
    /// <summary>Total length of the current track, or null when unknown
    /// (live/indeterminate). A null duration means the UI shows no scrub bar.</summary>
    TimeSpan? Duration { get; }

    /// <summary>Elapsed position within the current track.</summary>
    TimeSpan Position { get; }

    /// <summary>Whether <see cref="SeekAsync"/> does anything. Defaults false so
    /// read-only sources need not override it.</summary>
    bool CanSeek => false;

    /// <summary>Seek to <paramref name="position"/>. Default is a no-op for
    /// sources that can report position but not seek.</summary>
    Task SeekAsync(TimeSpan position) => Task.CompletedTask;
}
