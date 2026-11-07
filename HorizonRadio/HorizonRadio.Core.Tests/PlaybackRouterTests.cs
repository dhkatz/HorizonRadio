using HorizonRadio.Core.Events;
using HorizonRadio.Core.Games;
using HorizonRadio.Core.Models;
using HorizonRadio.Core.Playback;
using HorizonRadio.Core.Sources;
using HorizonRadio.Core.Sources.Config;
using HorizonRadio.Core.Sources.Mixes;
using HorizonRadio.Core.Sources.Queue;

namespace HorizonRadio.Core.Tests;

/// <summary>
/// The playback router: one engine, several channels (the player + one per mix). Runs real local
/// WAVs through the engine (paced in real time), so positions and track order are the engine's own.
/// </summary>
public class PlaybackRouterTests
{
    private sealed class NullSink : IPcmSink
    {
        public bool Send(ReadOnlySpan<short> samples) => true;
    }

    private sealed class ManualTime : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = DateTimeOffset.UnixEpoch;
        public override DateTimeOffset GetUtcNow() => Now;
    }

    private sealed class Harness : IAsyncDisposable
    {
        public SourceRunner Runner { get; }
        public QueuePlayback Player { get; }
        public MixStore Mixes { get; } = new();
        public PlaybackRouter Router { get; }
        public ManualTime Time { get; } = new();
        public List<string> Titles { get; } = new();

        public Harness()
        {
            var config = new SourceConfigStore();
            var resolver = new MixContentResolver(config);
            Runner = new SourceRunner(new NullSink());
            Player = new QueuePlayback(Runner, config, resolver);
            Router = new PlaybackRouter(Runner, Player, Mixes, resolver, Time);
            Runner.TrackChanged += t =>
            {
                if (t.SourceId != "local") return; // skip "Loading…" placeholders
                lock (Titles) Titles.Add(t.Title);
            };
        }

        public string? LastTitle
        {
            get { lock (Titles) return Titles.Count > 0 ? Titles[^1] : null; }
        }

        public int TitleCount
        {
            get { lock (Titles) return Titles.Count; }
        }

        public TimeSpan Position => (Runner.ActiveSource as IPlaybackProgress)?.Position ?? TimeSpan.Zero;

        public async ValueTask DisposeAsync()
        {
            await Router.DisposeAsync();
            await Runner.DisposeAsync();
            Player.Dispose();
        }
    }

    // A mix over its own folder of silent tracks named "<prefix>1".."<prefix>N" (played in order).
    private static Mix AddMix(Harness h, TempDir dir, string id, string prefix, int tracks, double seconds)
    {
        for (var i = 1; i <= tracks; i++) dir.WriteSilentWav($"{prefix}{i}.wav", seconds);
        var mix = new Mix(id, id, [new ContentRef("local", dir.Path)]);
        h.Mixes.AddOrUpdate(mix);
        return mix;
    }

    private static async Task WaitUntilAsync(Func<bool> condition, int timeoutMs = 8000)
    {
        var start = Environment.TickCount64;
        while (Environment.TickCount64 - start < timeoutMs)
        {
            if (condition()) return;
            await Task.Delay(30);
        }
        Assert.Fail("timed out waiting for the engine");
    }

    [Fact]
    public async Task Resume_continues_the_same_track_where_it_left_off()
    {
        await using var h = new Harness();
        using var a = TempDir.Create();
        using var b = TempDir.Create();
        AddMix(h, a, "A", "a", tracks: 2, seconds: 4);
        AddMix(h, b, "B", "b", tracks: 1, seconds: 4);

        await h.Router.RouteAsync(PlaybackRoute.Mix("A"));
        await WaitUntilAsync(() => h.LastTitle == "a1" && h.Position > TimeSpan.FromSeconds(0.8));
        var leftAt = h.Position;

        await h.Router.RouteAsync(PlaybackRoute.Mix("B"));
        await WaitUntilAsync(() => h.LastTitle == "b1");

        await h.Router.RouteAsync(PlaybackRoute.Mix("A"));
        await WaitUntilAsync(() => h.LastTitle == "a1" && h.TitleCount >= 3);
        // Same track, picked up near where it stopped rather than from the top. Playback is real
        // time, so a restart from 0 couldn't reach the old position within this short window;
        // a resumed seek gets there as soon as the item opens.
        await WaitUntilAsync(() => h.Position >= leftAt - TimeSpan.FromSeconds(0.2), timeoutMs: 250);
    }

    // A sink that remembers the most recent sample value it saw.
    private sealed class LastSampleSink : IPcmSink
    {
        private int _last = -1;
        public int Last => Volatile.Read(ref _last);

        public bool Send(ReadOnlySpan<short> samples)
        {
            if (samples.Length > 0) Volatile.Write(ref _last, samples[0]);
            return true;
        }
    }

    // A 44.1 kHz stereo WAV whose sample value encodes the playback time: it steps up by 100
    // every 0.1 s (sample = 100 * tenths), so the audio itself says where playback is.
    private static void WriteStaircaseWav(string path, double seconds)
    {
        var fmt = new NAudio.Wave.WaveFormat(44100, 16, 2);
        using var w = new NAudio.Wave.WaveFileWriter(path, fmt);
        var frames = (int)(seconds * 44100);
        var buf = new byte[frames * 4];
        for (var f = 0; f < frames; f++)
        {
            var value = (short)(100 * (f / 4410));
            BitConverter.TryWriteBytes(buf.AsSpan(f * 4), value);
            BitConverter.TryWriteBytes(buf.AsSpan(f * 4 + 2), value);
        }
        w.Write(buf, 0, buf.Length);
    }

    // Seconds of audio the last sample came from (the staircase's value / 100 tenths).
    private static double AudioSeconds(LastSampleSink sink) => Math.Round(sink.Last / 100.0) / 10.0;

    // Regression: the reported position resumed, but the AUDIO restarted from the top.
    [Fact]
    public async Task Resume_continues_the_audio_itself_not_just_the_position()
    {
        var sink = new LastSampleSink();
        var config = new SourceConfigStore();
        var resolver = new MixContentResolver(config);
        await using var runner = new SourceRunner(sink);
        using var player = new QueuePlayback(runner, config, resolver);
        var mixes = new MixStore();
        await using var router = new PlaybackRouter(runner, player, mixes, resolver);

        using var dir = TempDir.Create();
        WriteStaircaseWav(Path.Combine(dir.Path, "stairs.wav"), seconds: 6);
        using var other = TempDir.Create();
        other.WriteSilentWav("other.wav", 4);
        mixes.AddOrUpdate(new Mix("other", "Other", [new ContentRef("local", other.Path)]));

        await player.EnqueueLocatorAsync(SourceCatalog.Find("local")!, dir.Path);
        await WaitUntilAsync(() => AudioSeconds(sink) >= 1.5);
        var leftAt = AudioSeconds(sink);

        await router.RouteAsync(PlaybackRoute.Mix("other"));
        await Task.Delay(300);
        await router.RouteAsync(PlaybackRoute.Player);

        // The first audio after resuming must come from near where we left, not from 0.
        await WaitUntilAsync(() => sink.Last > 0 && AudioSeconds(sink) < 5.9, timeoutMs: 3000);
        await Task.Delay(150);
        Assert.True(AudioSeconds(sink) >= leftAt - 0.3, $"audio resumed at {AudioSeconds(sink)} s, left at {leftAt} s");
    }

    [Fact]
    public async Task Restart_starts_the_mix_fresh()
    {
        await using var h = new Harness();
        using var a = TempDir.Create();
        AddMix(h, a, "A", "a", tracks: 3, seconds: 0.4);

        await h.Router.RouteAsync(PlaybackRoute.Mix("A", StationSwitchMode.Restart));
        await WaitUntilAsync(() => h.LastTitle == "a2");

        await h.Router.RouteAsync(PlaybackRoute.Silent);
        await h.Router.RouteAsync(PlaybackRoute.Mix("A", StationSwitchMode.Restart));
        await WaitUntilAsync(() => h.TitleCount >= 3 && h.LastTitle == "a1");
    }

    [Fact]
    public async Task Live_locks_pause_and_moves_on_when_the_track_ended_while_away()
    {
        await using var h = new Harness();
        using var a = TempDir.Create();
        AddMix(h, a, "A", "a", tracks: 2, seconds: 4);

        await h.Router.RouteAsync(PlaybackRoute.Mix("A", StationSwitchMode.Live));
        await WaitUntilAsync(() => h.LastTitle == "a1");
        var controls = Assert.IsAssignableFrom<ITransportControls>(h.Runner.ActiveSource);
        Assert.False(controls.CanPause);
        await controls.TogglePauseAsync();
        Assert.False(controls.IsPaused);

        await h.Router.RouteAsync(PlaybackRoute.Silent);
        h.Time.Now += TimeSpan.FromSeconds(30); // a1 (4 s) would have ended long ago
        await h.Router.RouteAsync(PlaybackRoute.Mix("A", StationSwitchMode.Live));
        await WaitUntilAsync(() => h.LastTitle == "a2");
    }

    [Fact]
    public async Task Silent_parks_everything()
    {
        await using var h = new Harness();
        using var a = TempDir.Create();
        AddMix(h, a, "A", "a", tracks: 1, seconds: 2);

        await h.Router.RouteAsync(PlaybackRoute.Mix("A"));
        await WaitUntilAsync(() => h.LastTitle == "a1");
        await h.Router.RouteAsync(PlaybackRoute.Silent);

        Assert.Null(h.Runner.ActiveSource);
        Assert.Equal(PlaybackRouteKind.Silent, h.Router.Current.Kind);
    }

    [Fact]
    public async Task Player_additions_wait_until_the_player_is_routed()
    {
        await using var h = new Harness();
        using var a = TempDir.Create();
        using var q = TempDir.Create();
        AddMix(h, a, "A", "a", tracks: 1, seconds: 4);
        q.WriteSilentWav("queued.wav", 1);

        await h.Router.RouteAsync(PlaybackRoute.Mix("A"));
        await WaitUntilAsync(() => h.LastTitle == "a1");

        // Queued while a mix station is on: lands in the queue, doesn't take the engine.
        await h.Player.EnqueueLocatorAsync(SourceCatalog.Find("local")!, q.Path);
        Assert.False(h.Player.IsActive);
        Assert.Equal("a1", h.LastTitle);

        await h.Router.RouteAsync(PlaybackRoute.Player);
        await WaitUntilAsync(() => h.LastTitle == "queued");
        Assert.True(h.Player.IsActive);
    }

    // -- The player channel, driven step by step with fakes --

    private static async Task<ITransportControls> PlayAsync(Harness h, FakeItem first)
    {
        await h.Player.EnsureActiveAsync();
        await Eventually.Until(() => first.Plays == 1, first.Metadata.Title);
        return Assert.IsAssignableFrom<ITransportControls>(h.Runner.ActiveSource);
    }

    [Fact]
    public async Task Tuning_away_while_the_next_track_loads_does_not_bring_back_the_skipped_one()
    {
        await using var h = new Harness();
        var context = new FakeContext();
        var a = new FakeItem("a", TimeSpan.FromMinutes(3)) { At = TimeSpan.FromSeconds(30) };
        h.Player.Model.AppendExplicit([a]);
        h.Player.Model.SetContext(context, "m");
        var controls = await PlayAsync(h, a);

        await controls.NextAsync(); // a goes into history while the context resolves
        await Eventually.Until(() => context.Calls == 1, "the context to be asked for a track");
        await h.Router.RouteAsync(PlaybackRoute.Silent);
        await h.Router.RouteAsync(PlaybackRoute.Player);

        // It carries on to the next track rather than resuming a.
        await Eventually.Until(() => context.Calls == 2, "the context to be asked again");
        var next = new FakeItem("next");
        context.Feed(next);
        await Eventually.Until(() => next.Plays == 1, "the next track");
        Assert.Equal(1, a.Plays);

        // a is in history once: one step back reaches it, and there's nothing before it.
        await controls.PreviousAsync();
        await Eventually.Until(() => a.Plays == 2, "a again");
        Assert.False(controls.CanSkipPrevious);
    }

    [Fact]
    public async Task A_restart_before_a_resumed_track_gets_going_starts_it_from_the_top()
    {
        await using var h = new Harness();
        var a = new FakeItem("a", TimeSpan.FromMinutes(3));
        h.Player.Model.AppendExplicit([a]);
        var controls = await PlayAsync(h, a);

        a.At = TimeSpan.FromSeconds(30);
        await h.Router.RouteAsync(PlaybackRoute.Silent);
        a.HoldStart = FakeItem.NewSignal(); // starting again takes a moment (a resolve, say)
        await h.Router.RouteAsync(PlaybackRoute.Player);
        await Eventually.Until(() => a.Plays == 2, "the resumed play");

        await controls.RestartAsync();
        await Eventually.Until(() => a.Plays == 3, "the restarted play");
        a.HoldStart.TrySetResult();

        await Eventually.Until(() => a.StartPositions.Count == 2, "the restarted play to start");
        Assert.Equal([TimeSpan.Zero, TimeSpan.Zero], a.StartPositions);
    }

    [Fact]
    public async Task The_queue_shows_nothing_playing_once_another_source_takes_over()
    {
        await using var h = new Harness();
        var a = new FakeItem("a");
        h.Player.Model.AppendExplicit([a]);
        await PlayAsync(h, a);
        await Eventually.Until(() => h.Player.Model.Current?.Item == a, "a to show as playing");

        // Tuning away parks the player, which comes back to a: it still shows a.
        await h.Router.RouteAsync(PlaybackRoute.Silent);
        Assert.Same(a, h.Player.Model.Current?.Item);
        await h.Router.RouteAsync(PlaybackRoute.Player);
        await Eventually.Until(() => a.Plays == 2, "a again");

        // Another source taking over (Spotify Connect, the test tone) ends the queue's engine.
        await new ActionDispatcher(h.Runner, new SourceConfigStore())
            .RunAsync(new EventAction(EventActionType.SwitchSource, "testtone"));

        Assert.False(h.Player.IsActive);
        Assert.Null(h.Player.Model.Current);
    }

    [Fact]
    public async Task Switching_the_players_mix_while_tuned_away_plays_the_new_mix_on_return()
    {
        await using var h = new Harness();
        var oldMix = new FakeContext("old");
        var x = new FakeItem("old-1");
        oldMix.Feed(x);
        h.Player.Model.SetContext(oldMix, "old");
        await PlayAsync(h, x);
        await h.Router.RouteAsync(PlaybackRoute.Silent);

        // What playing a mix does while another channel is on: replace the context, and since
        // the player can't take the engine, nothing else.
        var newMix = new FakeContext("new");
        var y = new FakeItem("new-1");
        newMix.Feed(y);
        h.Player.Model.SetContext(newMix, "new", replaceNow: true);
        await h.Router.RouteAsync(PlaybackRoute.Player);

        await Eventually.Until(() => y.Plays == 1, "the new mix");
        Assert.Equal(1, x.Plays);
    }

    [Fact]
    public async Task Exposes_mixes_and_reports_changes()
    {
        await using var h = new Harness();
        var changes = 0;
        h.Router.MixesChanged += () => changes++;

        h.Mixes.AddOrUpdate(new Mix("m1", "Drive", []));

        Assert.Equal([new MixInfo("m1", "Drive")], h.Router.Mixes);
        Assert.Equal(1, changes);
    }
}
