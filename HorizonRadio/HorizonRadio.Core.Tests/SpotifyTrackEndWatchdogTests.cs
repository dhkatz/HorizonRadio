using HorizonRadio.Core.Sources.Spotify;

namespace HorizonRadio.Core.Tests;

/// <summary>
/// The end-wait of a driven Spotify track ends on the natural end, and fails the track
/// when librespot exits or its audio stops while playing, so the queue moves on instead
/// of sitting silent. A pause, however long, never ends a track.
/// </summary>
public class SpotifyTrackEndWatchdogTests
{
    private static readonly TimeSpan Stall = TimeSpan.FromSeconds(15);

    private static TrackWaitState Playing(long frames, long positionMs = 30_000, long durationMs = 200_000) =>
        new(EndEventSeen: false, PositionMs: positionMs, DurationMs: durationMs, Frames: frames,
            Suspended: false, LibrespotAlive: true);

    private static TimeSpan S(double seconds) => TimeSpan.FromSeconds(seconds);

    [Fact]
    public void Ends_on_librespots_end_event()
    {
        var w = new TrackEndWatchdog(Stall);
        Assert.Equal(TrackWaitVerdict.Ended, w.Check(Playing(100) with { EndEventSeen = true }, S(0)));
    }

    [Fact]
    public void Ends_when_all_the_audio_was_delivered()
    {
        var w = new TrackEndWatchdog(Stall);
        Assert.Equal(TrackWaitVerdict.Ended, w.Check(Playing(100, positionMs: 200_000), S(0)));
    }

    [Fact]
    public void Unknown_length_never_ends_on_position()
    {
        var w = new TrackEndWatchdog(Stall);
        Assert.Equal(TrackWaitVerdict.Continue, w.Check(Playing(100, positionMs: 999_000, durationMs: 0), S(0)));
    }

    [Fact]
    public void Keeps_waiting_while_audio_flows()
    {
        var w = new TrackEndWatchdog(Stall);
        for (var t = 0; t <= 60; t++)
            Assert.Equal(TrackWaitVerdict.Continue, w.Check(Playing(frames: t * 44_100), S(t)));
    }

    [Fact]
    public void Fails_when_audio_stops_while_playing()
    {
        var w = new TrackEndWatchdog(Stall);
        Assert.Equal(TrackWaitVerdict.Continue, w.Check(Playing(500), S(0)));
        Assert.Equal(TrackWaitVerdict.Continue, w.Check(Playing(500), S(14.5)));
        Assert.Equal(TrackWaitVerdict.Stalled, w.Check(Playing(500), S(15)));
    }

    [Fact]
    public void A_seek_rebasing_the_frame_count_is_progress()
    {
        var w = new TrackEndWatchdog(Stall);
        w.Check(Playing(500_000), S(0));
        Assert.Equal(TrackWaitVerdict.Continue, w.Check(Playing(0), S(10)));
        Assert.Equal(TrackWaitVerdict.Continue, w.Check(Playing(0), S(24)));
        Assert.Equal(TrackWaitVerdict.Stalled, w.Check(Playing(0), S(25)));
    }

    [Fact]
    public void A_long_pause_never_ends_the_track()
    {
        var w = new TrackEndWatchdog(Stall);
        w.Check(Playing(500), S(0));
        for (var t = 1; t <= 3600; t += 30)
            Assert.Equal(TrackWaitVerdict.Continue, w.Check(Playing(500) with { Suspended = true }, S(t)));
    }

    [Fact]
    public void The_stall_clock_starts_fresh_after_resuming()
    {
        var w = new TrackEndWatchdog(Stall);
        w.Check(Playing(500), S(0));
        w.Check(Playing(500) with { Suspended = true }, S(600));
        Assert.Equal(TrackWaitVerdict.Continue, w.Check(Playing(500), S(610)));
        Assert.Equal(TrackWaitVerdict.Stalled, w.Check(Playing(500), S(615)));
    }

    [Fact]
    public void Fails_at_once_when_librespot_exits()
    {
        var w = new TrackEndWatchdog(Stall);
        w.Check(Playing(500), S(0));
        Assert.Equal(TrackWaitVerdict.LibrespotExited, w.Check(Playing(500) with { LibrespotAlive = false }, S(0.5)));
    }

    [Fact]
    public void A_librespot_that_died_during_a_pause_is_left_to_the_resume_recovery()
    {
        var w = new TrackEndWatchdog(Stall);
        var dead = Playing(500) with { LibrespotAlive = false };
        Assert.Equal(TrackWaitVerdict.Continue, w.Check(dead with { Suspended = true }, S(0)));
        Assert.Equal(TrackWaitVerdict.Continue, w.Check(dead with { Suspended = true }, S(900)));
        Assert.Equal(TrackWaitVerdict.LibrespotExited, w.Check(dead, S(901)));
    }

    [Fact]
    public void A_finished_track_ends_even_if_librespot_exited()
    {
        var w = new TrackEndWatchdog(Stall);
        var finished = Playing(500, positionMs: 200_000) with { LibrespotAlive = false };
        Assert.Equal(TrackWaitVerdict.Ended, w.Check(finished, S(0)));
    }
}
