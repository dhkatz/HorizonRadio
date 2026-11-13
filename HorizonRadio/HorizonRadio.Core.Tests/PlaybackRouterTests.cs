using HorizonRadio.Core.Commands;
using HorizonRadio.Core.Events;
using HorizonRadio.Core.Playback;
using HorizonRadio.Core.Sources;
using HorizonRadio.Core.Sources.Config;
using HorizonRadio.Core.Sources.Mixes;
using HorizonRadio.Core.Sources.Queue;
using HorizonRadio.Sdk.Games;
using HorizonRadio.Sdk.Sources;
using HorizonRadio.Sdk.Sources.Config;

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

        // Mixes whose station channels play a fake context (see SetFakeMix) instead of files.
        private readonly Dictionary<string, FakeContext> _fakeMixes = new();
        private int _channelsBuilt;

        public Harness()
        {
            var config = new SourceConfigStore();
            var resolver = new MixContentResolver(config, TestSources.Catalog);
            Runner = new SourceRunner(new NullSink());
            Router = new PlaybackRouter(Runner, config, Mixes, resolver, Time);
            Player = Router.Player;
            Runner.TrackChanged += t =>
            {
                if (t.SourceId != "local") return; // skip "Loading…" placeholders
                lock (Titles) Titles.Add(t.Title);
            };

            var files = Router.MixContexts;
            Router.MixContexts = (mix, shuffle) =>
            {
                FakeContext? fake;
                lock (_fakeMixes) _fakeMixes.TryGetValue(mix.Id, out fake);
                if (fake == null) return files(mix, shuffle);
                Interlocked.Increment(ref _channelsBuilt);
                fake.SetShuffle(shuffle);
                return fake;
            };
        }

        /// <summary>Add (or edit) mix <paramref name="id"/>: its station plays
        /// <paramref name="context"/> from the next time its channel is built. A different
        /// <paramref name="version"/> is a different list of entries.</summary>
        public void SetFakeMix(string id, FakeContext context, string version = "1")
        {
            lock (_fakeMixes) _fakeMixes[id] = context;
            Mixes.AddOrUpdate(new Mix(id, id, [new ContentRef("test", version)]));
        }

        /// <summary>How many station channels were built from a fake mix.</summary>
        public int ChannelsBuilt => Volatile.Read(ref _channelsBuilt);

        /// <summary>Wait until the router has applied everything sent to it so far.</summary>
        public Task SettledAsync() => Router.RouteAsync(Router.Current);

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
        var resolver = new MixContentResolver(config, TestSources.Catalog);
        await using var runner = new SourceRunner(sink);
        var mixes = new MixStore();
        await using var router = new PlaybackRouter(runner, config, mixes, resolver);
        var player = router.Player;

        using var dir = TempDir.Create();
        WriteStaircaseWav(Path.Combine(dir.Path, "stairs.wav"), seconds: 6);
        using var other = TempDir.Create();
        other.WriteSilentWav("other.wav", 4);
        mixes.AddOrUpdate(new Mix("other", "Other", [new ContentRef("local", other.Path)]));

        await player.EnqueueLocatorAsync(TestSources.Catalog.Find("local")!, dir.Path);
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
        await h.Player.EnqueueLocatorAsync(TestSources.Catalog.Find("local")!, q.Path);
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
        await new CommandList(h.Router, NoLog.Instance)
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

    // -- One owner of the engine: commands from every side, applied one at a time --

    // A station whose channel plays `items` (each three minutes long, so they play until stopped).
    private static FakeContext Station(Harness h, string id, params FakeItem[] items)
    {
        var context = new FakeContext(id);
        foreach (var item in items) context.Feed(item);
        h.SetFakeMix(id, context);
        return context;
    }

    private static FakeItem Song(string title) => new(title, TimeSpan.FromMinutes(3));

    [Fact]
    public async Task A_source_started_while_the_station_changes_never_plays_alongside_it()
    {
        await using var h = new Harness();
        var release = FakeItem.NewSignal();
        var a = new FakeItem("a", TimeSpan.FromMinutes(3)) { HoldCancel = release }; // parking it takes a moment
        h.Player.Model.AppendExplicit([a]);
        await PlayAsync(h, a);
        var m1 = Song("m1");
        Station(h, "M", m1);
        var factory = new FakeSourceFactory();

        // The game tunes to the station while the user starts a source (the Sources tab, or a
        // bound "switch source"), both while the player is still winding down.
        var route = h.Router.RouteAsync(PlaybackRoute.Mix("M"));
        var start = Task.Run(() => h.Router.StartSourceAsync(factory, new ConfigValues()));
        await Task.Delay(200); // both under way, waiting on the player
        release.TrySetResult();
        await Task.WhenAll(route, start);
        await Task.Delay(200); // anything orphaned would be playing by now

        // Never both: one of them plays.
        var source = Assert.Single(factory.Created);
        var playing = (source.Running ? 1 : 0) + m1.Playing + a.Playing;
        Assert.True(playing == 1, $"{playing} playing (source: {source.Running}, station: {m1.Playing})");

        // And in the order asked for: the source the user started last takes over from the
        // station, which is parked.
        Assert.True(source.Running);
        Assert.Same(source, h.Runner.ActiveSource);
        Assert.Equal(PlaybackRoute.Player, h.Router.Current);
    }

    [Fact]
    public async Task Switches_from_every_side_at_once_never_leave_two_things_playing()
    {
        await using var h = new Harness();
        var songs = new List<FakeItem>();
        FakeItem Track(string title)
        {
            var song = Song(title);
            lock (songs) songs.Add(song);
            return song;
        }
        foreach (var id in new[] { "A", "B" })
            Station(h, id, [.. Enumerable.Range(1, 40).Select(i => Track($"{id}{i}"))]);
        var factory = new FakeSourceFactory();
        PlaybackRoute[] routes =
        [
            PlaybackRoute.Player, PlaybackRoute.Mix("A"), PlaybackRoute.Mix("B"), PlaybackRoute.Silent,
            PlaybackRoute.Mix("A", StationSwitchMode.Live), PlaybackRoute.Mix("B", StationSwitchMode.Restart),
        ];

        // The game tuning, the user starting and stopping sources, and the queue being added to,
        // each from its own thread.
        using var go = new Barrier(4);
        await Task.WhenAll(
            Task.Run(async () =>
            {
                go.SignalAndWait();
                for (var i = 0; i < 60; i++)
                {
                    h.Router.Route(routes[i % routes.Length]);
                    await Task.Delay(1);
                }
            }),
            Task.Run(async () =>
            {
                go.SignalAndWait();
                for (var i = 0; i < 15; i++) await h.Router.StartSourceAsync(factory, new ConfigValues());
            }),
            Task.Run(async () =>
            {
                go.SignalAndWait();
                for (var i = 0; i < 15; i++) await h.Router.StopAsync();
            }),
            Task.Run(async () =>
            {
                go.SignalAndWait();
                for (var i = 0; i < 30; i++)
                {
                    h.Player.Model.AppendExplicit([Track($"q{i}")]);
                    await h.Player.EnsureActiveAsync();
                }
            }));
        await h.SettledAsync();
        await Task.Delay(100);

        List<FakeItem> all;
        lock (songs) all = [.. songs];
        var playing = all.Sum(s => s.Playing) + factory.Created.Count(s => s.Running);
        Assert.True(playing <= 1, $"{playing} things playing at once");
        Assert.All(factory.Created.Where(s => s.Running), s => Assert.Same(s, h.Runner.ActiveSource));
    }

    [Fact]
    public async Task Adding_to_the_queue_while_the_player_is_left_loses_neither_that_nor_the_interrupted_track()
    {
        await using var h = new Harness();
        var a = Song("a");
        h.Player.Model.AppendExplicit([a]);
        await PlayAsync(h, a);
        a.At = TimeSpan.FromSeconds(30);
        Station(h, "M", Song("m1"));

        // The moment the runner lets go of the player on the way to the station, the user adds
        // to the queue.
        var added = Song("added");
        Task? enqueued = null;
        h.Runner.ActiveSourceChanged += _ =>
        {
            if (enqueued != null || h.Runner.ActiveSource != null) return;
            h.Player.Model.AppendExplicit([added]);
            enqueued = h.Player.EnsureActiveAsync();
        };
        await h.Router.RouteAsync(PlaybackRoute.Mix("M"));
        await enqueued!;

        // It waits in the queue while the station plays.
        Assert.False(h.Player.IsActive);
        Assert.Equal(1, a.Plays);

        // Back on the player: the interrupted track carries on where it was, then the new one.
        await h.Router.RouteAsync(PlaybackRoute.Player);
        await Eventually.Until(() => a.StartPositions.Count == 2, "a to resume");
        Assert.Equal(TimeSpan.FromSeconds(30), a.StartPositions[1]);
        a.End();
        await Eventually.Until(() => added.Plays == 1, "the added track");
    }

    [Fact]
    public async Task Stopping_a_station_keeps_its_place_for_when_the_game_routes_to_it_again()
    {
        await using var h = new Harness();
        var m1 = Song("m1");
        Station(h, "M", m1);
        await h.Router.RouteAsync(PlaybackRoute.Mix("M"));
        await Eventually.Until(() => m1.Plays == 1, "m1");
        m1.At = TimeSpan.FromSeconds(42);

        await h.Router.StopAsync(); // the Sources tab's Stop
        Assert.Null(h.Runner.ActiveSource);
        Assert.Equal(0, m1.Playing);

        // The game routes to the station again (the station's settings changed, say).
        await h.Router.RouteAsync(PlaybackRoute.Mix("M"));
        await Eventually.Until(() => m1.StartPositions.Count == 2, "m1 to resume");
        Assert.Equal(TimeSpan.FromSeconds(42), m1.StartPositions[1]);
        Assert.Equal(1, h.ChannelsBuilt);
    }

    [Fact]
    public async Task A_source_started_on_a_station_hands_the_engine_to_the_player()
    {
        await using var h = new Harness();
        var m1 = Song("m1");
        Station(h, "M", m1);
        await h.Router.RouteAsync(PlaybackRoute.Mix("M"));
        await Eventually.Until(() => m1.Plays == 1, "m1");
        m1.At = TimeSpan.FromSeconds(42);

        var factory = new FakeSourceFactory();
        await h.Router.StartSourceAsync(factory, new ConfigValues());
        var source = Assert.Single(factory.Created);
        Assert.True(source.Running);
        Assert.Equal(0, m1.Playing);
        Assert.Equal(PlaybackRoute.Player, h.Router.Current);

        // The queue takes over from it, as it would without a game.
        var q = Song("q");
        h.Player.Model.AppendExplicit([q]);
        await h.Player.EnsureActiveAsync();
        await Eventually.Until(() => q.Plays == 1, "q");
        Assert.False(source.Running);

        // Tuning back in to the station picks it up where the source cut in.
        await h.Router.RouteAsync(PlaybackRoute.Silent);
        await h.Router.RouteAsync(PlaybackRoute.Mix("M"));
        await Eventually.Until(() => m1.StartPositions.Count == 2, "m1 to resume");
        Assert.Equal(TimeSpan.FromSeconds(42), m1.StartPositions[1]);
    }

    [Fact]
    public async Task Changing_a_stations_switch_mode_keeps_its_song_playing()
    {
        await using var h = new Harness();
        var m1 = Song("m1");
        Station(h, "M", m1);
        await h.Router.RouteAsync(PlaybackRoute.Mix("M", StationSwitchMode.Restart));
        await Eventually.Until(() => m1.Plays == 1, "m1");
        var controls = Assert.IsAssignableFrom<ITransportControls>(h.Runner.ActiveSource);

        await h.Router.RouteAsync(PlaybackRoute.Mix("M", StationSwitchMode.Live));
        Assert.False(controls.CanPause);
        await h.Router.RouteAsync(PlaybackRoute.Mix("M", StationSwitchMode.Resume));
        Assert.True(controls.CanPause);

        // Never stopped, restarted or rebuilt.
        Assert.Equal(1, m1.Plays);
        Assert.Equal(1, m1.Playing);
        Assert.Same(controls, h.Runner.ActiveSource);
        Assert.Equal(1, h.ChannelsBuilt);
        Assert.Equal(PlaybackRoute.Mix("M", StationSwitchMode.Resume), h.Router.Current);
    }

    [Fact]
    public async Task Shuffle_changed_while_a_channel_is_parked_applies_when_it_comes_back()
    {
        await using var h = new Harness();
        var player = new FakeContext("player's mix");
        var p1 = Song("p1");
        player.Feed(p1);
        h.Player.Model.SetContext(player, "pm");
        await PlayAsync(h, p1);
        var station = Station(h, "S", Song("s1"));

        await h.Router.RouteAsync(PlaybackRoute.Mix("S"));
        Assert.False(station.Shuffled);

        // On, while the player is parked: the station takes it now, the player when it's back.
        await h.Router.SetShuffleAsync(true);
        Assert.True(station.Shuffled);
        Assert.False(player.Shuffled);
        await h.Router.RouteAsync(PlaybackRoute.Player);
        Assert.True(player.Shuffled);

        // Off, while the station is parked.
        await h.Router.SetShuffleAsync(false);
        Assert.False(player.Shuffled);
        Assert.True(station.Shuffled);
        await h.Router.RouteAsync(PlaybackRoute.Mix("S"));
        Assert.False(station.Shuffled);
    }

    [Fact]
    public async Task Editing_a_mix_rebuilds_its_parked_station_and_deleting_it_frees_the_channel()
    {
        await using var h = new Harness();
        var old1 = Song("old1");
        Station(h, "M", old1);
        await h.Router.RouteAsync(PlaybackRoute.Mix("M"));
        await Eventually.Until(() => old1.Plays == 1, "the mix");
        var oldChannel = Assert.IsType<QueueSource>(h.Runner.ActiveSource);
        await h.Router.RouteAsync(PlaybackRoute.Silent);

        // Renaming it doesn't change what it plays: the station keeps its place.
        h.Mixes.AddOrUpdate(new Mix("M", "Renamed", [new ContentRef("test", "1")]));
        await h.SettledAsync();
        Assert.False(oldChannel.IsDisposed);

        // Edited while tuned away: the station plays the new version, from its start.
        var new1 = Song("new1");
        var edited = new FakeContext("v2");
        edited.Feed(new1);
        h.SetFakeMix("M", edited, version: "2");
        await h.SettledAsync();
        Assert.True(oldChannel.IsDisposed);
        await h.Router.RouteAsync(PlaybackRoute.Mix("M"));
        await Eventually.Until(() => new1.Plays == 1, "the edited mix");
        Assert.Equal(1, old1.Plays);

        // Deleted while tuned away: its channel goes with it.
        var newChannel = Assert.IsType<QueueSource>(h.Runner.ActiveSource);
        await h.Router.RouteAsync(PlaybackRoute.Silent);
        h.Mixes.Remove("M");
        await h.SettledAsync();
        Assert.True(newChannel.IsDisposed);
    }

    [Fact]
    public async Task Editing_the_mix_on_air_lets_the_song_play_on_and_rebuilds_the_station_after()
    {
        await using var h = new Harness();
        var old1 = Song("old1");
        Station(h, "M", old1);
        await h.Router.RouteAsync(PlaybackRoute.Mix("M"));
        await Eventually.Until(() => old1.Plays == 1, "the mix");
        var oldChannel = Assert.IsType<QueueSource>(h.Runner.ActiveSource);

        var new1 = Song("new1");
        var edited = new FakeContext("v2");
        edited.Feed(new1);
        h.SetFakeMix("M", edited, version: "2");
        await h.SettledAsync();
        Assert.Equal(1, old1.Playing);
        Assert.Same(oldChannel, h.Runner.ActiveSource);

        await h.Router.RouteAsync(PlaybackRoute.Silent);
        Assert.True(oldChannel.IsDisposed);
        await h.Router.RouteAsync(PlaybackRoute.Mix("M"));
        await Eventually.Until(() => new1.Plays == 1, "the edited mix");
        Assert.Equal(1, old1.Plays);
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
