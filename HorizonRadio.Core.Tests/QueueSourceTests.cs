using HorizonRadio.Core.Models;
using HorizonRadio.Core.Sources;
using HorizonRadio.Core.Sources.Config;
using HorizonRadio.Core.Sources.Mixes;
using HorizonRadio.Core.Sources.Queue;

namespace HorizonRadio.Core.Tests;

/// <summary>
/// End-to-end checks of the queue engine over real local items: it plays explicit
/// items before the context, pumps each item's PCM, publishes a track per item, and
/// the mix context refills the tail forever (loops past one lap). When the explicit
/// zone drains with no context it parks idle rather than ending.
/// </summary>
public class QueueSourceTests
{
    private sealed class CountingSink : IPcmSink
    {
        private long _samples;
        public long Samples => Interlocked.Read(ref _samples);
        public bool Send(ReadOnlySpan<short> samples)
        {
            Interlocked.Add(ref _samples, samples.Length);
            return true;
        }
    }

    private static MixContentResolver Resolver() => new(new SourceConfigStore());

    private static async Task<IReadOnlyList<PlayableItem>> ResolveAsync(string locator)
        => await Resolver().EnumerateAsync(new ContentRef("local", locator), CancellationToken.None);

    private static int CountLocal(List<Track> tracks)
    {
        lock (tracks) return tracks.Count(t => t.SourceId == "local");
    }

    private static async Task WaitUntilAsync(Func<bool> condition, int timeoutMs = 5000)
    {
        var start = Environment.TickCount64;
        while (Environment.TickCount64 - start < timeoutMs)
        {
            if (condition()) return;
            await Task.Delay(40);
        }
    }

    [Fact]
    public async Task Context_plays_entry_items_and_publishes_tracks()
    {
        using var dir = TempDir.Create();
        dir.WriteSilentWav("a.wav", seconds: 0.15);
        dir.WriteSilentWav("b.wav", seconds: 0.15);

        var mix = new Mix("t", "Test", [new ContentRef("local", dir.Path)]);
        var model = new QueueModel();
        model.SetContext(new MixContextProvider(mix, Resolver()), "t");

        await using var src = new QueueSource(model);
        var tracks = new List<Track>();
        src.TrackChanged += t => { lock (tracks) tracks.Add(t); };

        var sink = new CountingSink();
        using var cts = new CancellationTokenSource();
        await src.StartAsync(sink, cts.Token);

        await WaitUntilAsync(() => CountLocal(tracks) >= 2);
        await src.StopAsync();

        Assert.True(sink.Samples > 0, "expected PCM to be pumped");
        Assert.True(CountLocal(tracks) >= 2, $"expected >=2 local tracks, got {CountLocal(tracks)}");
    }

    [Fact]
    public async Task Explicit_items_play_before_the_context()
    {
        using var dir = TempDir.Create();
        var x = dir.WriteSilentWav("xfirst.wav", seconds: 0.12);
        using var ctxDir = TempDir.Create();
        ctxDir.WriteSilentWav("ylater.wav", seconds: 0.12);

        var model = new QueueModel();
        model.AppendExplicit(await ResolveAsync(x));
        var mix = new Mix("c", "Ctx", [new ContentRef("local", ctxDir.Path)]);
        model.SetContext(new MixContextProvider(mix, Resolver()), "c");

        await using var src = new QueueSource(model);
        var titles = new List<string>();
        src.TrackChanged += t =>
        {
            if (t.SourceId != "local") return;
            lock (titles) if (titles.Count == 0 || titles[^1] != t.Title) titles.Add(t.Title);
        };

        using var cts = new CancellationTokenSource();
        await src.StartAsync(new CountingSink(), cts.Token);

        await WaitUntilAsync(() =>
        {
            lock (titles) return titles.Contains("xfirst") && titles.Contains("ylater");
        });
        await src.StopAsync();

        lock (titles)
        {
            var xi = titles.IndexOf("xfirst");
            var yi = titles.IndexOf("ylater");
            Assert.True(xi >= 0 && yi >= 0, $"both should play; saw [{string.Join(",", titles)}]");
            Assert.True(xi < yi, "the explicit item must play before the context");
        }
    }

    [Fact]
    public async Task Context_refills_past_one_lap()
    {
        using var dir = TempDir.Create();
        dir.WriteSilentWav("a.wav", seconds: 0.1);
        dir.WriteSilentWav("b.wav", seconds: 0.1);

        var mix = new Mix("t", "Loop", [new ContentRef("local", dir.Path)]);
        var model = new QueueModel();
        model.SetContext(new MixContextProvider(mix, Resolver()), "t");

        await using var src = new QueueSource(model);
        var count = 0;
        src.TrackChanged += t => { if (t.SourceId == "local") Interlocked.Increment(ref count); };

        using var cts = new CancellationTokenSource();
        await src.StartAsync(new CountingSink(), cts.Token);

        // More than the two items proves the entry order wrapped and replayed.
        await WaitUntilAsync(() => Volatile.Read(ref count) >= 4, timeoutMs: 8000);
        await src.StopAsync();

        Assert.True(Volatile.Read(ref count) >= 4, $"expected the context to loop; got {count} tracks");
    }

    [Fact]
    public async Task Drains_to_idle_when_explicit_empties_with_no_context()
    {
        using var dir = TempDir.Create();
        var only = dir.WriteSilentWav("only.wav", seconds: 0.12);

        var model = new QueueModel();
        model.AppendExplicit(await ResolveAsync(only));

        await using var src = new QueueSource(model);
        var localTracks = 0;
        src.TrackChanged += t => { if (t.SourceId == "local") Interlocked.Increment(ref localTracks); };

        using var cts = new CancellationTokenSource();
        await src.StartAsync(new CountingSink(), cts.Token);

        await WaitUntilAsync(() => Volatile.Read(ref localTracks) >= 1);
        // Let the single item finish and the engine park.
        await Task.Delay(600);

        Assert.Equal(1, Volatile.Read(ref localTracks)); // nothing further played
        Assert.False(src.CanSkipNext, "with nothing queued and no context, next is disabled");

        await src.StopAsync();
    }
}
