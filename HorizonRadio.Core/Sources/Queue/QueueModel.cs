using System;
using System.Collections.Generic;
using HorizonRadio.Core.Sources.Mixes;

namespace HorizonRadio.Core.Sources.Queue;

/// <summary>
/// The queue's state and the one place it is mutated — independent of the engine
/// that plays it. Decoupling the two is what lets the queue survive a self-driven
/// source (Spotify Connect) taking over playback: the engine (<see cref="QueueSource"/>)
/// is recreated on each return to content, but this model — the explicit items and
/// the active mix context — persists for the app's lifetime.
///
/// Two zones, Spotify-style: an explicit list of user one-offs played first, then a
/// single infinite <see cref="MixContextProvider"/> context that refills the tail
/// forever. The model holds the data; the engine drives it.
///
/// Thread-safety: UI-thread callers mutate via the public methods; the engine
/// thread consumes via the internal ones. All access is under <see cref="_lock"/>;
/// events are raised after the lock is released so handlers can call back in.
/// </summary>
public sealed class QueueModel
{
    private readonly object _lock = new();
    private readonly List<QueueItem> _explicit = new();
    private MixContextProvider? _context;
    private string? _contextMixId;
    private QueueItem? _current;
    private bool _currentFromContext;

    /// <summary>Raised after any change to the queue's contents or now-playing,
    /// so the sidebar can re-snapshot. Fires on the mutating thread.</summary>
    public event Action? Changed;

    /// <summary>Raised when work becomes available while the engine may be idle
    /// (explicit items added, or a context set). The engine wakes on it.</summary>
    public event Action? WorkAvailable;

    /// <summary>Raised when the engine should stop the current track and re-pick
    /// (a play-now jump, or a context replace that should take effect now).</summary>
    public event Action? InterruptRequested;

    /// <summary>Id of the mix currently feeding the tail, or null. Read by station
    /// targeting (the mix's override applies only while its context is active).</summary>
    public string? ContextMixId { get { lock (_lock) return _contextMixId; } }

    public bool HasContext { get { lock (_lock) return _context != null; } }

    /// <summary>The item playing now, or null — a cheap read (no snapshot/peek).</summary>
    public QueueItem? Current { get { lock (_lock) return _current; } }

    /// <summary>The active context generator, or null. The engine pulls from it
    /// and the sidebar peeks it; both guard their own access.</summary>
    public MixContextProvider? Context { get { lock (_lock) return _context; } }

    /// <summary>Whether anything is left to play — used by the engine's idle
    /// re-check and to gate transport's "next". A context only counts if it can
    /// actually yield tracks; an entry-less context yields null forever, so
    /// counting it here would spin the engine's idle loop instead of parking it.</summary>
    public bool HasWork { get { lock (_lock) return _explicit.Count > 0 || (_context?.HasEntries ?? false); } }

    // -- engine-side consumption --

    /// <summary>Take (and remove) the front explicit item, or null if none. The
    /// item is now "playing", not "in queue", so it leaves the explicit list.</summary>
    internal QueueItem? TakeExplicitFront()
    {
        QueueItem item;
        lock (_lock)
        {
            if (_explicit.Count == 0) return null;
            item = _explicit[0];
            _explicit.RemoveAt(0);
        }
        Changed?.Invoke();
        return item;
    }

    internal void SetNowPlaying(QueueItem? item, bool fromContext)
    {
        lock (_lock) { _current = item; _currentFromContext = fromContext; }
        Changed?.Invoke();
    }

    // -- UI-side mutation --

    /// <summary>Append resolved items to the explicit ("next in queue") zone.</summary>
    public void AppendExplicit(IEnumerable<PlayableItem> items)
    {
        var added = false;
        lock (_lock)
        {
            foreach (var it in items) { _explicit.Add(new QueueItem(it)); added = true; }
        }
        if (!added) return;
        Changed?.Invoke();
        WorkAvailable?.Invoke();
    }

    public void RemoveExplicit(string id)
    {
        bool removed;
        lock (_lock) removed = _explicit.RemoveAll(q => q.Id == id) > 0;
        if (removed) Changed?.Invoke();
    }

    /// <summary>Move an explicit item to the current position of <paramref name="targetId"/>
    /// (drag-and-drop reorder). No-op if either id is gone or it's a self-drop.</summary>
    public void MoveExplicitTo(string id, string targetId)
    {
        if (id == targetId) return;
        var moved = false;
        lock (_lock)
        {
            var src = _explicit.FindIndex(q => q.Id == id);
            if (src < 0) return;
            var item = _explicit[src];
            _explicit.RemoveAt(src);
            var tgt = _explicit.FindIndex(q => q.Id == targetId);
            if (tgt < 0) tgt = _explicit.Count; // target gone → drop at the end
            _explicit.Insert(tgt, item);
            moved = tgt != src;
        }
        if (moved) Changed?.Invoke();
    }

    /// <summary>Nudge an explicit item up (delta &lt; 0) or down (delta &gt; 0),
    /// clamped to the list bounds.</summary>
    public void MoveExplicit(string id, int delta)
    {
        var moved = false;
        lock (_lock)
        {
            var i = _explicit.FindIndex(q => q.Id == id);
            if (i >= 0)
            {
                var j = Math.Clamp(i + delta, 0, _explicit.Count - 1);
                if (j != i) { var it = _explicit[i]; _explicit.RemoveAt(i); _explicit.Insert(j, it); moved = true; }
            }
        }
        if (moved) Changed?.Invoke();
    }

    /// <summary>Play an explicit item now: drop the items queued before it (they're
    /// skipped) and interrupt the current track so the engine picks it up next.</summary>
    public void JumpToExplicit(string id)
    {
        bool found;
        lock (_lock)
        {
            var i = _explicit.FindIndex(q => q.Id == id);
            found = i >= 0;
            if (i > 0) _explicit.RemoveRange(0, i);
        }
        if (!found) return;
        Changed?.Invoke();
        InterruptRequested?.Invoke();
    }

    public void ClearExplicit()
    {
        bool any;
        lock (_lock) { any = _explicit.Count > 0; _explicit.Clear(); }
        if (any) Changed?.Invoke();
    }

    /// <summary>Set (or clear) the infinite context the queue draws from once the
    /// explicit zone is empty. <paramref name="replaceNow"/> interrupts the current
    /// track so a station switch takes effect immediately rather than after the
    /// playing track ends.</summary>
    public void SetContext(MixContextProvider? provider, string? mixId, bool replaceNow = false)
    {
        lock (_lock) { _context = provider; _contextMixId = mixId; }
        Changed?.Invoke();
        if (provider != null) WorkAvailable?.Invoke();
        if (replaceNow) InterruptRequested?.Invoke();
    }

    public QueueSnapshot Snapshot(int contextPeek = 50)
    {
        QueueItem? current;
        bool fromContext;
        List<QueueItem> snapshot;
        MixContextProvider? context;
        string? name;
        lock (_lock)
        {
            current = _current;
            fromContext = _currentFromContext;
            snapshot = new List<QueueItem>(_explicit);
            context = _context;
            name = context?.DisplayName;
        }
        // Peek outside our lock — the provider guards its own state, and this
        // avoids holding two locks at once.
        var peek = context?.Peek(contextPeek) ?? (IReadOnlyList<QueuePreview>)Array.Empty<QueuePreview>();
        return new QueueSnapshot(current, fromContext, snapshot, name, peek);
    }
}
