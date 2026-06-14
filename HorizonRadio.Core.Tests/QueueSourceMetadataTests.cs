using HorizonRadio.Core.Models;
using HorizonRadio.Core.Sources;
using HorizonRadio.Core.Sources.Queue;

namespace HorizonRadio.Core.Tests;

/// <summary>
/// Guards the cross-cutting change that internet radio needs: a single playing item
/// whose metadata changes mid-stream (a station rolling to a new song) republishes the
/// now-playing track via <see cref="PumpContext.OnMetadataUpdated"/>, so the queue
/// engine re-fires <see cref="QueueSource.TrackChanged"/> for each song.
/// </summary>
public class QueueSourceMetadataTests
{
    private sealed class CountingSink : IPcmSink
    {
        public bool Send(ReadOnlySpan<short> samples) => true;
    }

    // A fake "live" item: announces once, then emits N mid-stream metadata updates, then
    // parks until cancelled — exactly the shape of a radio station.
    private sealed class LiveItem : PlayableItem
    {
        private readonly int _updates;
        public LiveItem(int updates)
        {
            _updates = updates;
            Metadata = new Track("Station", "", null, null, "radio", "Internet Radio");
        }

        public override async Task PlayAsync(PumpContext ctx, CancellationToken ct)
        {
            ctx.OnStarted?.Invoke(this);
            for (int i = 0; i < _updates; i++)
            {
                Metadata = Metadata with { Title = $"Song {i}", ExternalId = $"radio:s{i}" };
                ctx.OnMetadataUpdated?.Invoke(this);
            }
            await Task.Delay(Timeout.Infinite, ct); // a station ends only on skip/stop
        }
    }

    private static async Task WaitUntilAsync(Func<bool> condition, int timeoutMs = 5000)
    {
        var start = Environment.TickCount64;
        while (Environment.TickCount64 - start < timeoutMs)
        {
            if (condition()) return;
            await Task.Delay(20);
        }
    }

    [Fact]
    public async Task Mid_stream_metadata_updates_refire_track_changed()
    {
        var model = new QueueModel();
        model.AppendExplicit([new LiveItem(3)]);

        await using var src = new QueueSource(model);
        var titles = new List<string>();
        src.TrackChanged += t => { lock (titles) titles.Add(t.Title); };

        using var cts = new CancellationTokenSource();
        await src.StartAsync(new CountingSink(), cts.Token);

        await WaitUntilAsync(() => { lock (titles) return titles.Contains("Song 2"); });
        await src.StopAsync();

        lock (titles)
        {
            Assert.Contains("Station", titles);  // initial OnStarted
            Assert.Contains("Song 0", titles);   // mid-stream OnMetadataUpdated
            Assert.Contains("Song 1", titles);
            Assert.Contains("Song 2", titles);
        }
    }
}
