using HorizonRadio.Core.Sources;
using HorizonRadio.Core.Sources.Config;
using HorizonRadio.Core.Sources.Local;
using HorizonRadio.Core.Sources.Mixes;
using HorizonRadio.Core.Sources.Queue;

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
}
