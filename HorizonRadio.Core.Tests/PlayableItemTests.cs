using HorizonRadio.Core.Sources;
using HorizonRadio.Core.Sources.Local;

namespace HorizonRadio.Core.Tests;

/// <summary>
/// The item layer (the "B seam"): a content player expands a ref into ordered
/// <see cref="PlayableItem"/>s, and a local item pumps real PCM to a sink and
/// reports its position. These are what the mix engine sequences.
/// </summary>
public class PlayableItemTests
{
    private sealed class CountingSink : IPcmSink
    {
        public long Samples;
        public bool Send(ReadOnlySpan<short> samples)
        {
            Samples += samples.Length;
            return true;
        }
    }

    [Fact]
    public async Task Local_enumerate_returns_files_in_order()
    {
        using var dir = TempDir.Create();
        // Created out of order; the loader sorts case-insensitively by path.
        dir.Touch("c.mp3");
        dir.Touch("a.mp3");
        dir.Touch("b.flac");

        var items = await new LocalContentPlayer().EnumerateAsync(new ContentRef("local", dir.Path), default);

        var titles = items.Select(i => i.Metadata.Title).ToArray();
        Assert.Equal(new[] { "a", "b", "c" }, titles);
        Assert.All(items, i => Assert.Equal("local", i.Metadata.SourceId));
    }

    [Fact]
    public async Task Local_enumerate_empty_folder_throws()
    {
        using var dir = TempDir.Create();
        dir.Touch("readme.txt");
        var player = new LocalContentPlayer();
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => player.EnumerateAsync(new ContentRef("local", dir.Path), default));
        Assert.Contains("no audio files", ex.Message);
    }

    [Fact]
    public async Task Local_item_pumps_pcm_and_reports_position()
    {
        using var dir = TempDir.Create();
        var wav = dir.WriteSilentWav("tone.wav", seconds: 0.3);
        var item = new LocalPlayableItem(wav);

        PlayableItem? started = null;
        var sink = new CountingSink();
        await item.PlayAsync(new PumpContext { Sink = sink, OnStarted = i => started = i }, default);

        Assert.Same(item, started);                       // OnStarted fired with the item
        Assert.True(sink.Samples > 0, "expected PCM to be pumped");
        Assert.NotNull(item.Duration);
        Assert.InRange(item.Position.TotalSeconds, 0.2, 0.5); // ended near the file length
    }

    [Fact]
    public void Local_item_is_seekable_and_reflects_position_immediately()
    {
        using var dir = TempDir.Create();
        var wav = dir.WriteSilentWav("tone.wav", seconds: 1.0);
        var item = new LocalPlayableItem(wav);

        Assert.True(item.CanSeek);
        item.Seek(TimeSpan.FromSeconds(0.5));
        Assert.Equal(0.5, item.Position.TotalSeconds, precision: 2); // reflected before the pump runs
    }

    [Fact]
    public async Task Local_item_cancelled_token_throws()
    {
        using var dir = TempDir.Create();
        var wav = dir.WriteSilentWav("tone.wav", seconds: 0.3);
        var item = new LocalPlayableItem(wav);

        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        await Assert.ThrowsAsync<OperationCanceledException>(
            () => item.PlayAsync(new PumpContext { Sink = new CountingSink() }, cts.Token));
    }
}
