using HorizonRadio.Core.Sources.Queue;

namespace HorizonRadio.Core.Tests;

/// <summary>A queue shared with a listening party's host: the user's own is set aside, what they do
/// to it is asked of the host, and the engine plays only what the party puts on.</summary>
public sealed class QueueSharingTests
{
    [Fact]
    public void Sharing_sets_your_queue_aside_and_asks_the_owner_instead()
    {
        var model = new QueueModel();
        var (a, b) = (new FakeItem("a"), new FakeItem("b"));
        model.AppendExplicit([a, b]);
        model.SetContext(new FakeContext("Mine"), "m");
        var owner = new RecordingQueueOwner();
        model.Share(owner);

        var snap = model.Snapshot();
        Assert.True(snap.Shared);
        Assert.Empty(snap.Explicit);
        Assert.Null(snap.ContextName);
        Assert.False(model.HasWork);

        model.AppendExplicit([new FakeItem("c")], playNow: true);
        model.RemoveExplicit("x");
        model.MoveExplicitTo("x", "y");
        model.MoveExplicit("x", 1);
        model.JumpToExplicit("x");
        model.ClearExplicit();
        Assert.Equal(["add c now", "remove x", "move x to y", "move x by 1", "play x", "clear"], owner.Asked);
        Assert.Empty(model.Snapshot().Explicit);

        // The host's queue as it sent it, and its next song first in line to load ahead.
        QueueItem[] hosts = [new(new FakeItem("h1"), id: "h1"), new(new FakeItem("h2"), id: "h2")];
        model.Mirror(hosts, "Sam's Mix", [new QueuePreview("h3", "", false)]);
        snap = model.Snapshot();
        Assert.Equal(["h1", "h2"], snap.Explicit.Select(q => q.Id));
        Assert.Equal("Sam's Mix", snap.ContextName);
        Assert.Equal("h3", Assert.Single(snap.ContextPeek).Title);
        Assert.Same(hosts[0], model.PeekNext().Item);

        // A mix played meanwhile is yours, for after the party.
        var other = new FakeContext("Other");
        model.SetContext(other, "o");
        Assert.Null(model.Context);

        model.Unshare();
        snap = model.Snapshot();
        Assert.False(snap.Shared);
        Assert.Equal([a, b], snap.Explicit.Select(q => q.Item));
        Assert.Same(other, model.Context);
        Assert.Null(model.Owner);
    }

    [Fact]
    public async Task A_shared_queue_plays_what_the_party_puts_on_loads_the_hosts_next_ahead_and_asks_to_skip()
    {
        var model = new QueueModel();
        var owner = new RecordingQueueOwner();
        model.Share(owner);
        var next = new FakeItem("next");
        model.Mirror([new QueueItem(next, id: "k2")], null, []);
        await using var src = new QueueSource(model) { PrefetchAfter = TimeSpan.FromMilliseconds(50) };
        await src.StartAsync(new NullPcmSink(), CancellationToken.None);
        await Task.Delay(200);
        Assert.Equal(0, next.Plays); // the host's queue isn't played by itself

        var now = new FakeItem("now");
        model.PlayNext(now, id: "k1");
        await Eventually.Until(() => now.Plays == 1, "the party's song to play");
        Assert.Equal("k1", model.Current!.Id);
        await Eventually.Until(() => next.Prefetches > 0, "the host's next song to be loaded ahead");

        // Skips are the host's to make: the song plays on until the party puts the next one on.
        Assert.True(src.CanSkipNext);
        Assert.True(src.CanSkipPrevious);
        await src.NextAsync();
        await src.PreviousAsync();
        Assert.Equal(["skip", "back"], owner.Asked);
        await Task.Delay(100);
        Assert.Equal(1, now.Playing);

        // It ends before the host's next song is on: nothing plays meanwhile.
        now.End();
        await Eventually.Until(() => model.Current == null, "the engine to wait for the party");
        Assert.Equal(0, next.Plays);
    }
}
