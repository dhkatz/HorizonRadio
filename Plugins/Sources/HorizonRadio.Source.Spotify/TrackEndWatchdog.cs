namespace HorizonRadio.Source.Spotify;

/// <summary>What the end-wait of one Spotify track should do next.</summary>
internal enum TrackWaitVerdict
{
    /// <summary>Keep waiting: audio is flowing, or the transport is paused.</summary>
    Continue,

    /// <summary>The track finished: librespot's end event, or all of its audio delivered.</summary>
    Ended,

    /// <summary>librespot is gone (process exited or its PCM pipe closed); nothing more will play.</summary>
    LibrespotExited,

    /// <summary>No audio has arrived for the stall timeout while playing.</summary>
    Stalled,
}

/// <summary>One look at the playing track, taken by the end-wait loop.</summary>
/// <param name="EndEventSeen">librespot reported the track's end (already gated on its length).</param>
/// <param name="PositionMs">Playback position (audio delivered), ms.</param>
/// <param name="DurationMs">Track length in ms, or 0 when unknown.</param>
/// <param name="Frames">The frame counter behind the position. Any change (more audio, or a
/// seek rebasing it) counts as progress.</param>
/// <param name="Suspended">The user paused, or the pause watcher is still pausing/resuming
/// the device. Audio stops by design meanwhile, and a dead librespot is the resume
/// recovery's to relaunch, so neither ends the track.</param>
/// <param name="LibrespotAlive">The librespot process is running and its PCM pipe is open.</param>
internal readonly record struct TrackWaitState(
    bool EndEventSeen, long PositionMs, long DurationMs, long Frames, bool Suspended, bool LibrespotAlive);

/// <summary>
/// Decides when the end-wait of a Spotify track is over. Besides the natural end, it
/// catches the two ways a track can go quiet with no end event: librespot exiting, and
/// audio stopping while we're not paused (a wedged session, or the user moving playback to
/// another device). The stall clock only runs while playing, so a long pause never ends a
/// track. Pure apart from its own progress bookkeeping: the caller supplies the time.
/// </summary>
internal sealed class TrackEndWatchdog(TimeSpan stallTimeout)
{
    private long _lastFrames = -1;
    private TimeSpan _lastProgress;

    public TrackWaitVerdict Check(in TrackWaitState state, TimeSpan now)
    {
        if (state.EndEventSeen || (state.DurationMs > 0 && state.PositionMs >= state.DurationMs))
            return TrackWaitVerdict.Ended;

        // Audio moved, or we're paused: restart the stall clock. A pause therefore never
        // counts toward a stall, and the clock starts fresh once playback resumes.
        if (state.Suspended || state.Frames != _lastFrames)
        {
            _lastFrames = state.Frames;
            _lastProgress = now;
        }

        if (state.Suspended) return TrackWaitVerdict.Continue;
        if (!state.LibrespotAlive) return TrackWaitVerdict.LibrespotExited;
        return now - _lastProgress >= stallTimeout ? TrackWaitVerdict.Stalled : TrackWaitVerdict.Continue;
    }
}
