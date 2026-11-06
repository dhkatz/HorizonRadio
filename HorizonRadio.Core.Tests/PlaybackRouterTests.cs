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
