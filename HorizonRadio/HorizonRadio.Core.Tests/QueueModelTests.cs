using HorizonRadio.Core.Sources.Config;
using HorizonRadio.Core.Sources.Mixes;
using HorizonRadio.Core.Sources.Queue;
using HorizonRadio.Sdk.Sources;
using HorizonRadio.Source.Local;

namespace HorizonRadio.Core.Tests;

/// <summary>
/// The queue's state surface, independent of the engine: explicit append/order,
/// play-now (jump drops the skipped items and asks for an interrupt), remove/move,
/// and setting a context (which records the mix id and signals work).
/// </summary>
public class QueueModelTests
{
    // A no-I/O playable used only to populate the model; it's never pumped here.
    private static LocalPlayableItem Item(string name) => new($"{name}.mp3");

    [Fact]
    public void Append_adds_in_order_and_signals_work()
    {
        var model = new QueueModel();
        var changed = 0; var work = 0;
        model.Changed += () => changed++;
        model.WorkAvailable += () => work++;

        model.AppendExplicit([Item("a"), Item("b")]);

        var snap = model.Snapshot();
        Assert.Equal(new[] { "a", "b" }, snap.Explicit.Select(q => q.Metadata.Title));
        Assert.True(changed >= 1);
        Assert.Equal(1, work);
    }

    [Fact]
    public void JumpTo_drops_skipped_items_and_requests_interrupt()
    {
        var model = new QueueModel();
        model.AppendExplicit([Item("a"), Item("b"), Item("c")]);
        var interrupts = 0;
        model.InterruptRequested += () => interrupts++;

        var second = model.Snapshot().Explicit[1].Id; // "b"
        model.JumpToExplicit(second);

        var titles = model.Snapshot().Explicit.Select(q => q.Metadata.Title).ToArray();
        Assert.Equal(new[] { "b", "c" }, titles); // "a" was skipped
        Assert.Equal(1, interrupts);
    }

    [Fact]
    public void Remove_and_move_reorder_the_explicit_zone()
    {
        var model = new QueueModel();
        model.AppendExplicit([Item("a"), Item("b"), Item("c")]);

        var bId = model.Snapshot().Explicit[1].Id;
        model.RemoveExplicit(bId);
        Assert.Equal(new[] { "a", "c" }, model.Snapshot().Explicit.Select(q => q.Metadata.Title));

        var cId = model.Snapshot().Explicit[1].Id;
        model.MoveExplicit(cId, -1); // c up to the front
        Assert.Equal(new[] { "c", "a" }, model.Snapshot().Explicit.Select(q => q.Metadata.Title));
    }

    [Fact]
    public void MoveExplicitTo_reorders_to_targets_position()
    {
        var model = new QueueModel();
        model.AppendExplicit([Item("a"), Item("b"), Item("c")]);
        var ids = model.Snapshot().Explicit.Select(q => q.Id).ToArray();

        model.MoveExplicitTo(ids[0], ids[2]); // drop "a" onto "c" → lands at c's slot

        Assert.Equal(new[] { "b", "a", "c" }, model.Snapshot().Explicit.Select(q => q.Metadata.Title));
    }

    [Fact]
    public void SetContext_records_mix_id_and_signals_work()
    {
        var model = new QueueModel();
        var work = 0;
        model.WorkAvailable += () => work++;

        var mix = new Mix("m", "M", [new ContentRef("local", "C:/music")]);
        model.SetContext(new MixContextProvider(mix, new MixContentResolver(new SourceConfigStore())), "m");

        Assert.True(model.HasContext);
        Assert.Equal("m", model.ContextMixId);
        Assert.Equal("M", model.Snapshot().ContextName);
        Assert.Equal(1, work);
    }

    [Fact]
    public async Task SetShuffle_reorders_the_context_peek_now_and_signals_a_change()
    {
        using var dir = TempDir.Create();
        foreach (var n in new[] { "a", "b", "c", "d", "e", "f" }) dir.Touch($"{n}.mp3");
        var provider = new MixContextProvider(
            new Mix("m", "M", [new ContentRef("local", dir.Path)]), new MixContentResolver(new SourceConfigStore()));
        var model = new QueueModel();
        model.SetContext(provider, "m");
        Assert.Equal("a", (await provider.NextAsync(CancellationToken.None))?.Metadata.Title);

        var changed = 0;
        model.Changed += () => changed++;
        model.SetShuffle(true);

        Assert.Equal(1, changed);
        var upcoming = model.Snapshot().ContextPeek.Select(p => p.Title).ToList();
        Assert.Equal(["b", "c", "d", "e", "f"], upcoming.Order());
        for (var i = 0; i < upcoming.Count; i++)
            Assert.Equal(upcoming[i], (await provider.NextAsync(CancellationToken.None))?.Metadata.Title);
    }
}
