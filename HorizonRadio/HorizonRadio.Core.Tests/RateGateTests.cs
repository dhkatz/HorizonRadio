using HorizonRadio.Sdk.Metadata;
using Microsoft.Extensions.Time.Testing;

namespace HorizonRadio.Core.Tests;

/// <summary>
/// The providers' rate limit: one caller at a time, spaced apart, with the song playing
/// now (a foreground ticket) let through ahead of background list enrichment.
///
/// The gate runs on a clock only the test moves, one interval at a time, so who goes next never
/// depends on how fast the runner is (on the real clock a busy CI runner let callers through
/// before the rest had queued, or before one could give up).
/// </summary>
public class RateGateTests
{
    private static readonly TimeSpan Interval = TimeSpan.FromSeconds(1);

    private readonly FakeTimeProvider _time = new();

    // Queue a caller holding the given ticket. The gate reads the ambient ticket when the
    // caller arrives, exactly as a provider inside a resolve would.
    private static (string Name, Task Task) Enqueue(RateGate gate, PriorityTicket? ticket, string name,
                                                    CancellationToken ct = default)
    {
        MetadataPriority.Current = ticket;
        var wait = gate.WaitAsync(ct);
        MetadataPriority.Current = null;
        return (name, wait);
    }

    // Lets the callers through, one interval at a time, and names them in the order they went.
    private async Task<List<string>> GrantAll(params (string Name, Task Task)[] callers)
    {
        var order = new List<string>();
        var pending = callers.ToList();
        while (pending.Count > 0)
        {
            await Task.WhenAny(pending.Select(p => p.Task)).WaitAsync(TimeSpan.FromSeconds(5));
            var granted = pending.Where(p => p.Task.IsCompleted).ToList();
            Assert.Single(granted); // the clock stood still: one caller per interval
            Assert.True(granted[0].Task.IsCompletedSuccessfully, $"{granted[0].Name} wasn't let through");
            order.Add(granted[0].Name);
            pending.Remove(granted[0]);
            _time.Advance(Interval);
        }
        return order;
    }

    [Fact]
    public async Task Callers_are_spaced_by_the_interval()
    {
        using var gate = new RateGate(Interval, _time);

        await gate.WaitAsync(CancellationToken.None); // the first goes straight through
        var second = gate.WaitAsync(CancellationToken.None);

        _time.Advance(Interval - TimeSpan.FromMilliseconds(1));
        await Task.Delay(50);
        Assert.False(second.IsCompleted);

        _time.Advance(TimeSpan.FromMilliseconds(1));
        await second.WaitAsync(TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task Foreground_goes_ahead_of_queued_background()
    {
        using var gate = new RateGate(Interval, _time);
        var background = new PriorityTicket(ResolvePriority.Background);

        var a = Enqueue(gate, background, "a"); // let through immediately
        var b = Enqueue(gate, background, "b");
        var c = Enqueue(gate, background, "c");
        var now = Enqueue(gate, new PriorityTicket(ResolvePriority.Foreground), "now");

        Assert.Equal(["a", "now", "b", "c"], await GrantAll(a, b, c, now));
    }

    [Fact]
    public async Task Promotion_while_queued_takes_effect()
    {
        using var gate = new RateGate(Interval, _time);
        var promoted = new PriorityTicket(ResolvePriority.Background);

        var a = Enqueue(gate, new PriorityTicket(ResolvePriority.Background), "a");
        var b = Enqueue(gate, new PriorityTicket(ResolvePriority.Background), "b");
        var c = Enqueue(gate, promoted, "c");
        promoted.Promote(); // the song playing now joined c's lookup

        Assert.Equal(["a", "c", "b"], await GrantAll(a, b, c));
    }

    [Fact]
    public async Task No_ticket_counts_as_foreground()
    {
        using var gate = new RateGate(Interval, _time);
        var background = new PriorityTicket(ResolvePriority.Background);

        var a = Enqueue(gate, background, "a");
        var b = Enqueue(gate, background, "b");
        var direct = Enqueue(gate, null, "direct");

        Assert.Equal(["a", "direct", "b"], await GrantAll(a, b, direct));
    }

    [Fact]
    public async Task A_caller_that_gives_up_loses_its_turn_without_blocking_others()
    {
        using var gate = new RateGate(Interval, _time);
        using var cts = new CancellationTokenSource();

        var a = Enqueue(gate, null, "a");
        var gaveUp = Enqueue(gate, null, "gave up", cts.Token);
        var c = Enqueue(gate, null, "c");
        await cts.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => gaveUp.Task);
        Assert.Equal(["a", "c"], await GrantAll(a, c));
    }
}
