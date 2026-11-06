using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;

namespace HorizonRadio.Core.Metadata;

/// <summary>
/// A polite client-side rate limit: callers are let through one at a time, at least
/// <c>minInterval</c> apart. Shared by the metadata providers, which each hit an external API
/// that tolerates bursts poorly (MusicBrainz asks for ~1 req/s; iTunes/VocaDB just shouldn't be
/// hammered).
///
/// Who goes next is decided when the slot opens, not when a caller arrives: a caller whose
/// <see cref="MetadataPriority"/> ticket is foreground (the song playing now) goes ahead of
/// background list enrichment, first come first served within each. Without that, opening a
/// big mix queued the now-playing lookup behind every row's.
/// </summary>
public sealed class RateGate : IDisposable
{
    private readonly TimeSpan _minInterval;
    private readonly object _lock = new();
    private readonly LinkedList<Waiter> _queue = new();
    private readonly Stopwatch _clock = Stopwatch.StartNew();
    private TimeSpan? _lastGrant;
    private bool _pumping;
    private bool _disposed;

    public RateGate(TimeSpan minInterval) => _minInterval = minInterval;

    private sealed class Waiter(PriorityTicket? ticket)
    {
        public readonly TaskCompletionSource Done = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public CancellationTokenRegistration Registration;

        // No ticket = asked for directly, outside any resolve: treat as foreground.
        public bool IsForeground => ticket?.IsForeground ?? true;
    }

    /// <summary>Wait for this caller's turn: at least <c>minInterval</c> after the previous caller
    /// was let through, with foreground callers ahead of background ones.</summary>
    public Task WaitAsync(CancellationToken ct)
    {
        if (ct.IsCancellationRequested) return Task.FromCanceled(ct);

        var waiter = new Waiter(MetadataPriority.Current);
        lock (_lock)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            _queue.AddLast(waiter);
            if (!_pumping)
            {
                _pumping = true;
                _ = PumpAsync();
            }
        }
        if (ct.CanBeCanceled)
            waiter.Registration = ct.Register(() => Abandon(waiter, ct));
        return waiter.Done.Task;
    }

    // A caller gave up while queued. If it was already let through, its turn is spent.
    private void Abandon(Waiter waiter, CancellationToken ct)
    {
        lock (_lock)
        {
            if (!_queue.Remove(waiter)) return;
        }
        waiter.Done.TrySetCanceled(ct);
    }

    // Lets queued callers through, one per interval, until the queue is empty.
    private async Task PumpAsync()
    {
        while (true)
        {
            TimeSpan wait;
            lock (_lock)
            {
                if (_queue.Count == 0)
                {
                    _pumping = false;
                    return;
                }
                wait = _lastGrant is { } last ? last + _minInterval - _clock.Elapsed : TimeSpan.Zero;
            }
            if (wait > TimeSpan.Zero) await Task.Delay(wait).ConfigureAwait(false);

            Waiter? next;
            lock (_lock)
            {
                next = PickNext();
                if (next == null) continue; // everyone queued gave up during the wait
                _queue.Remove(next);
                _lastGrant = _clock.Elapsed;
            }
            next.Registration.Dispose();
            next.Done.TrySetResult();
        }
    }

    // The first foreground caller, else the first caller. Read now, so a promotion while
    // queued counts.
    private Waiter? PickNext()
    {
        for (var node = _queue.First; node != null; node = node.Next)
            if (node.Value.IsForeground) return node.Value;
        return _queue.First?.Value;
    }

    public void Dispose()
    {
        List<Waiter> abandoned;
        lock (_lock)
        {
            if (_disposed) return;
            _disposed = true;
            abandoned = [.. _queue];
            _queue.Clear();
        }
        foreach (var waiter in abandoned)
        {
            waiter.Registration.Dispose();
            waiter.Done.TrySetException(new ObjectDisposedException(nameof(RateGate)));
        }
    }
}
