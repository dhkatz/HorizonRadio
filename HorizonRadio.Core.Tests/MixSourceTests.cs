using HorizonRadio.Core.Models;
using HorizonRadio.Core.Sources;
using HorizonRadio.Core.Sources.Config;
using HorizonRadio.Core.Sources.Mixes;

namespace HorizonRadio.Core.Tests;

/// <summary>
/// End-to-end check of the mix engine over real local items: it expands an
/// entry, pumps each item's PCM, and publishes a track per item as it starts.
/// </summary>
public class MixSourceTests
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

    [Fact]
    public async Task MixSource_plays_entry_items_and_publishes_tracks()
    {
        using var dir = TempDir.Create();
        dir.WriteSilentWav("a.wav", seconds: 0.15);
        dir.WriteSilentWav("b.wav", seconds: 0.15);

        var mix = new Mix("t", "Test", [new ContentRef("local", dir.Path)]);
        var resolver = new MixContentResolver(new SourceConfigStore());
        await using var src = new MixSource(mix, resolver);

        var tracks = new List<Track>();
        src.TrackChanged += t => { lock (tracks) tracks.Add(t); };

        var sink = new CountingSink();
        using var cts = new CancellationTokenSource();
        await src.StartAsync(sink, cts.Token);

        // Both files should play through (2 real tracks past the "Loading…"
        // placeholder, which carries the mix's own SourceId rather than "local").
        var start = Environment.TickCount64;
        while (Environment.TickCount64 - start < 4000)
        {
            if (CountLocal(tracks) >= 2) break;
            await Task.Delay(50);
        }
        await src.StopAsync();

        Assert.True(sink.Samples > 0, "expected PCM to be pumped");
        Assert.True(CountLocal(tracks) >= 2, $"expected >=2 local tracks, got {CountLocal(tracks)}");
    }

    private static int CountLocal(List<Track> tracks)
    {
        lock (tracks) return tracks.Count(t => t.SourceId == "local");
    }
}
