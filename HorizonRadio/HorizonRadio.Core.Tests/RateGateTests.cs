using System.Diagnostics;
using HorizonRadio.Core.Metadata;

namespace HorizonRadio.Core.Tests;

/// <summary>
/// The providers' rate limit: one caller at a time, spaced apart, with the song playing
/// now (a foreground ticket) let through ahead of background list enrichment.
/// </summary>
public class RateGateTests
{
    private static readonly TimeSpan Interval = TimeSpan.FromMilliseconds(60);

    // For the ordering tests: the first caller goes straight through and the next waits one interval,
    // so every caller has to be queued within it. A busy CI runner can stall the test thread past
    // 60 ms (the gate then rightly lets the second caller through first), so they get half a second.
    private static readonly TimeSpan OrderingInterval = TimeSpan.FromMilliseconds(500);

    // Queue a caller holding the given ticket. The gate reads the ambient ticket when the
    // caller arrives, exactly as a provider inside a resolve would.
    private static Task Enqueue(RateGate gate, PriorityTicket? ticket, string name,
                                GrantOrder order, CancellationToken ct = default)
    {
        MetadataPriority.Current = ticket;
        var wait = gate.WaitAsync(ct);
        MetadataPriority.Current = null;
        order.Watch(name, wait);
        return wait;
    }

    /// <summary>
    /// The order the gate lets callers through. A caller's task completes the moment it's let
    /// through, but continuations on it run on the thread pool, which a busy CI runner can starve
    /// for longer than an interval (seen: the first caller recorded after the second). A thread of
    /// its own watching the tasks isn't affected.
    /// </summary>
    private sealed class GrantOrder : IDisposable
    {
        private readonly List<(string Name, Task Task)> _callers = [];
        private readonly List<string> _order = [];
        private readonly Thread _watcher;
        private volatile bool _stop;

        public GrantOrder()
        {
            _watcher = new Thread(Run) { IsBackground = true, Name = "rate-gate-order" };
            _watcher.Start();
        }

        public void Watch(string name, Task task)
        {
            lock (_callers) _callers.Add((name, task));
        }

        private void Run()
        {
            var seen = new HashSet<string>();
            while (!_stop)
            {
                lock (_callers)
                {
                    foreach (var (name, task) in _callers)
                        if (task.IsCompletedSuccessfully && seen.Add(name))
                            lock (_order) _order.Add(name);
                }
                Thread.Sleep(1);
            }
        }

        /// <summary>Everyone let through so far, once the watcher has seen them all.</summary>
        public IReadOnlyList<string> Order()
        {
            int granted;
            lock (_callers) granted = _callers.Count(c => c.Task.IsCompletedSuccessfully);
            var deadline = Stopwatch.StartNew();
            while (deadline.Elapsed < TimeSpan.FromSeconds(5))
            {
                lock (_order)
                    if (_order.Count >= granted) return [.. _order];
                Thread.Sleep(1);
            }
            lock (_order) return [.. _order];
        }

        public void Dispose()
        {
            _stop = true;
            _watcher.Join();
        }
    }

    [Fact]
    public async Task Callers_are_spaced_by_the_interval()
    {
        using var gate = new RateGate(Interval);
        var clock = Stopwatch.StartNew();

        await gate.WaitAsync(CancellationToken.None);
        await gate.WaitAsync(CancellationToken.None);
        await gate.WaitAsync(CancellationToken.None);

        // The first goes straight through; the other two each wait one interval.
        Assert.True(clock.Elapsed >= Interval * 2 - TimeSpan.FromMilliseconds(15), $"took {clock.Elapsed}");
    }

    [Fact]
    public async Task Foreground_goes_ahead_of_queued_background()
    {
        using var gate = new RateGate(OrderingInterval);
        using var order = new GrantOrder();
        var background = new PriorityTicket(ResolvePriority.Background);

        var a = Enqueue(gate, background, "a", order); // let through immediately
        var b = Enqueue(gate, background, "b", order);
        var c = Enqueue(gate, background, "c", order);
        var now = Enqueue(gate, new PriorityTicket(ResolvePriority.Foreground), "now", order);
        await Task.WhenAll(a, b, c, now);

        Assert.Equal(["a", "now", "b", "c"], order.Order());
    }

    [Fact]
    public async Task Promotion_while_queued_takes_effect()
    {
        using var gate = new RateGate(OrderingInterval);
        using var order = new GrantOrder();
        var promoted = new PriorityTicket(ResolvePriority.Background);

        var a = Enqueue(gate, new PriorityTicket(ResolvePriority.Background), "a", order);
        var b = Enqueue(gate, new PriorityTicket(ResolvePriority.Background), "b", order);
        var c = Enqueue(gate, promoted, "c", order);
        promoted.Promote(); // the song playing now joined c's lookup
        await Task.WhenAll(a, b, c);

        Assert.Equal(["a", "c", "b"], order.Order());
    }

    [Fact]
    public async Task No_ticket_counts_as_foreground()
    {
        using var gate = new RateGate(OrderingInterval);
        using var order = new GrantOrder();
        var background = new PriorityTicket(ResolvePriority.Background);

        var a = Enqueue(gate, background, "a", order);
        var b = Enqueue(gate, background, "b", order);
        var direct = Enqueue(gate, null, "direct", order);
        await Task.WhenAll(a, b, direct);

        Assert.Equal(["a", "direct", "b"], order.Order());
    }

    [Fact]
    public async Task A_caller_that_gives_up_loses_its_turn_without_blocking_others()
    {
        using var gate = new RateGate(OrderingInterval);
        using var order = new GrantOrder();
        using var cts = new CancellationTokenSource();

        var a = Enqueue(gate, null, "a", order);
        MetadataPriority.Current = null;
        var gaveUp = gate.WaitAsync(cts.Token);
        var c = Enqueue(gate, null, "c", order);
        await cts.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => gaveUp);
        await Task.WhenAll(a, c);
        Assert.Equal(["a", "c"], order.Order());
    }
}
