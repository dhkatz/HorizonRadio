using HorizonRadio.Sdk.Models;
using HorizonRadio.Sdk.Sources;

namespace HorizonRadio.Core.Tests;

/// <summary>
/// An item is prepared from several places at once (the engine warming it, the queue reading
/// ahead, the item starting to play). Every caller must see it fully prepared: a local file
/// was announced by its file name ("02" as the artist) because playback found it "already
/// prepared" while the queue's tag read was still running.
/// </summary>
public class PlayableItemPrepareTests
{
    private sealed class SlowItem : PlayableItem
    {
        public readonly TaskCompletionSource Release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public int Runs;

        public SlowItem() => Metadata = new Track("WATCH ME", "02", null, null, "local", "Local Files");

        public override Task PrepareAsync(CancellationToken ct) => PrepareOnceAsync(async token =>
        {
            Interlocked.Increment(ref Runs);
            await Release.Task.WaitAsync(token);
            Metadata = Metadata with { Artist = "K/DA" };
        }, ct);

        public override Task PlayAsync(PumpContext ctx, CancellationToken ct) => Task.CompletedTask;
    }

    [Fact]
    public async Task A_second_caller_waits_for_the_first_ones_work()
    {
        var item = new SlowItem();
        var readAhead = item.PrepareAsync(CancellationToken.None);
        var play = item.PrepareAsync(CancellationToken.None);

        Assert.False(play.IsCompleted); // not "already prepared" while the read is running
        item.Release.SetResult();
        await Task.WhenAll(readAhead, play);

        Assert.Equal("K/DA", item.Metadata.Artist);
        Assert.Equal(1, item.Runs);
    }

    [Fact]
    public async Task A_cancelled_attempt_is_started_again_for_the_next_caller()
    {
        var item = new SlowItem();
        using var skip = new CancellationTokenSource();
        var warm = item.PrepareAsync(skip.Token);
        var play = item.PrepareAsync(CancellationToken.None);

        await skip.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => warm);
        item.Release.SetResult();
        await play;

        Assert.Equal("K/DA", item.Metadata.Artist);
        Assert.Equal(2, item.Runs);
    }

    [Fact]
    public async Task Once_prepared_it_is_not_prepared_again()
    {
        var item = new SlowItem();
        item.Release.SetResult();
        await item.PrepareAsync(CancellationToken.None);
        await item.PrepareAsync(CancellationToken.None);

        Assert.Equal(1, item.Runs);
    }
}
