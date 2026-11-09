using HorizonRadio.Core.Sources.Spotify;
using Xunit;

namespace HorizonRadio.Core.Tests;

// Events as Soloist 1.3.10 sent them, played a single track to its end (`soloist ctl trace`).
public sealed class SoloistTrackTests
{
    private const string Firestarter = "spotify:track:6UeSC8jEhf9XmUL8EX2rni";

    private static string TrackChanged(string uri, long durationMs) =>
        """{"type":"track_changed","item":{"uri":""" + "\"" + uri + "\"" +
        ""","entity_type":"track","decorations":{"identity":{"name":"x"},"playback":{"duration_ms":""" +
        durationMs.ToString(System.Globalization.CultureInfo.InvariantCulture) + "}}}}";

    private static string Status(string status) => $$$"""{"type":"playback_changed","status":"{{{status}}}"}""";

    private static string Position(long ms, long stamp, double speed) =>
        $$$"""{"type":"position_sync","position":{"position_ms":{{{ms}}},"timestamp_ms":{{{stamp}}},"speed":{{{speed}}}}}""";

    private const string PlayAccepted = """{"type":"command_result","command":"play"}""";

    private static List<SoloistTrackStep> Feed(SoloistTrack track, params string[] events) =>
        [.. events.Select(json => track.Apply(SoloistEvent.Parse(json)!)).Where(s => s != SoloistTrackStep.None)];

    [Fact]
    public void Follows_a_single_track_to_its_end_and_ignores_the_autoplay_after_it()
    {
        var track = new SoloistTrack(Firestarter, null, () => false);
        var steps = Feed(track,
            Status("playing"),                         // the old context resuming, before the switch
            PlayAccepted,
            TrackChanged(Firestarter, 187280),
            Status("buffering"),
            Status("playing"),
            Position(180003, 1000, 1),
            Status("buffering"),                       // the end
            TrackChanged("spotify:track:3s2YCL5LHLzXWbw7ezQru3", 212000), // Soloist's autoplay
            Status("playing"));

        Assert.Equal([SoloistTrackStep.Started, SoloistTrackStep.Playing, SoloistTrackStep.Ended], steps);
        Assert.Equal(TimeSpan.FromMilliseconds(187280), track.Duration);
    }

    [Fact]
    public void A_relinked_track_counts_as_the_one_asked_for()
    {
        var track = new SoloistTrack(Firestarter, null, () => false);
        var steps = Feed(track, PlayAccepted, TrackChanged("spotify:track:relinked", 187280), Status("playing"));
        Assert.Equal([SoloistTrackStep.Started, SoloistTrackStep.Playing], steps);
        Assert.False(track.Ended);
    }

    [Fact]
    public void The_track_activation_resumes_before_the_play_isnt_this_one()
    {
        // Becoming the active device resumes what Soloist last had (here its autoplay pick).
        var track = new SoloistTrack(Firestarter, null, () => false);
        var steps = Feed(track,
            TrackChanged("spotify:track:3s2YCL5LHLzXWbw7ezQru3", 212000),
            Status("playing"),
            PlayAccepted,
            TrackChanged(Firestarter, 187280),
            Status("playing"));
        Assert.Equal([SoloistTrackStep.Started, SoloistTrackStep.Playing], steps);
        Assert.False(track.Ended);
    }

    [Fact]
    public void Stopping_at_the_end_is_the_end_but_a_pause_midway_is_not()
    {
        var paused = false;
        var track = new SoloistTrack(Firestarter, TimeSpan.FromSeconds(187), () => paused);
        Feed(track, PlayAccepted, TrackChanged(Firestarter, 187000), Status("playing"), Position(60000, 1000, 1));

        paused = true;
        Assert.Equal(SoloistTrackStep.None, track.Apply(SoloistEvent.Parse(Status("paused"))!));
        paused = false;
        Assert.Equal(SoloistTrackStep.None, track.Apply(SoloistEvent.Parse(Status("paused"))!)); // not near the end

        Feed(track, Status("playing"), Position(186000, 2000, 0));
        Assert.Equal(SoloistTrackStep.Ended, track.Apply(SoloistEvent.Parse(Status("paused"))!)); // autoplay off
    }

    [Fact]
    public void Replaying_the_loaded_track_starts_from_its_snapshot()
    {
        var track = new SoloistTrack(Firestarter, null, () => false);
        var snapshot = """{"type":"playback_state","status":"playing","item":{"uri":"spotify:track:6UeSC8jEhf9XmUL8EX2rni","decorations":{"playback":{"duration_ms":187280}}},"position":{"position_ms":2,"timestamp_ms":1000,"speed":1}}""";
        Assert.Equal(SoloistTrackStep.None, track.Apply(SoloistEvent.Parse(snapshot)!)); // before the play: activation
        track.Apply(SoloistEvent.Parse(PlayAccepted)!);
        Assert.Equal(SoloistTrackStep.Playing, track.Apply(SoloistEvent.Parse(snapshot)!));
        Assert.True(track.Started);
    }

    [Fact]
    public void Position_runs_on_from_the_last_anchor_and_stops_at_the_end()
    {
        var track = new SoloistTrack(Firestarter, null, () => false);
        Feed(track, PlayAccepted, TrackChanged(Firestarter, 187280), Status("playing"), Position(180000, 10_000, 1));

        Assert.Equal(TimeSpan.FromMilliseconds(182500), track.PositionAt(12_500));
        Assert.Equal(TimeSpan.FromMilliseconds(187280), track.PositionAt(99_000));
    }

    [Fact]
    public void Reads_login_state_and_errors()
    {
        var auth = SoloistEvent.Parse("""{"type": "auth_state", "logged_in": true, "is_active": false, "device_name": "Horizon Radio (Soloist)"}""")!;
        Assert.Equal(("auth_state", true, false), (auth.Type, auth.LoggedIn, auth.IsActive));
        Assert.Equal("command requires authentication",
            SoloistEvent.Parse("""{"type":"error","message":"command requires authentication"}""")!.Message);
        Assert.Null(SoloistEvent.Parse("not json"));
    }
}
