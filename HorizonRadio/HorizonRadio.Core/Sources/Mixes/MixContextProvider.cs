using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using HorizonRadio.Core.Audio;
using HorizonRadio.Core.Sources.Queue;

namespace HorizonRadio.Core.Sources.Mixes;

/// <summary>
/// Turns a <see cref="Mix"/> into an infinite, lazy track generator that feeds
/// the global queue's tail. This is the sequencing half of the old MixSource —
/// the two-level <see cref="PlayOrder"/> (entries, and items within the current
/// entry), grouped two-level shuffle, and the continuous entry-level loop — split
/// out from the PCM pumping so the queue engine can drive it one track at a time.
///
/// <see cref="NextAsync"/> resolves entries lazily (a YouTube entry's enumerate
/// takes a beat) and loops forever: when the entry order wraps it reshuffles for a
/// fresh pass, exactly as a radio replacement wants. <see cref="Peek"/> reports a
/// best-effort rolling window of what's upcoming without consuming anything —
/// resolved items of the current entry, then a placeholder per upcoming entry.
///
/// Threading: the order state is advanced on the engine thread via
/// <see cref="NextAsync"/>, reshuffled by <see cref="SetShuffle"/> and read by
/// <see cref="Peek"/> from the UI thread, so all three go through <see cref="_lock"/>;
/// the expensive enumerate happens outside it.
/// </summary>
public sealed class MixContextProvider : IQueueContext
{
    private readonly Mix _mix;
    private readonly MixContentResolver _resolver;
    private readonly object _lock = new();

    private readonly PlayOrder _entryOrder = new();
    private PlayOrder _itemOrder = new();
    private List<PlayableItem> _items = new();
    private bool _initialized;
    private bool _itemsLoaded;
    private int _idleEntries;

    // Both cursors point at what's playing now: the entry cursor at the current
    // entry, the item cursor at the item last handed out (it moves on at the start of
    // the next NextAsync). That way a shuffle toggle pins the playing song at both
    // levels. False until the loaded entry's first item is handed out.
    private bool _itemHandedOut;

    private bool _shuffle;

    // The next entry, loaded ahead (PeekNextAsync) for NextAsync to take as it is: its items, and
    // the order they play in once drawn, so what was loaded ahead is what plays.
    private sealed class Ahead(int entry, Task<List<PlayableItem>> items)
    {
        public int Entry => entry;
        public Task<List<PlayableItem>> Items => items;
        public PlayOrder? Order { get; set; }
    }

    private Ahead? _ahead;

    public MixContextProvider(Mix mix, MixContentResolver resolver, bool shuffle = false)
    {
        _mix = mix;
        _resolver = resolver;
        _shuffle = shuffle;
    }

    public string DisplayName => _mix.Name;
    public bool HasEntries => _mix.Entries.Count > 0;

    /// <summary>How long to pause after a full lap of the mix produced no audio,
    /// to avoid a tight retry loop when every entry is unplayable.</summary>
    private static readonly TimeSpan IdleBackoff = TimeSpan.FromSeconds(3);

    private static void Log(string msg) => Trace.WriteLine($"[hzn-mix-ctx] {msg}");

    /// <summary>
    /// Turn shuffle on or off, now, at both order levels (so <see cref="Peek"/> shows the
    /// new order straight away). The playing song stays put and is never replayed;
    /// turning shuffle on reorders only what hasn't played yet this pass, turning it off
    /// resumes the natural order after the playing song.
    /// </summary>
    public void SetShuffle(bool enabled)
    {
        lock (_lock)
        {
            _shuffle = enabled;
            if (!_initialized) return; // applied when the order is first built

            _entryOrder.SetShuffle(enabled, keepCurrent: true, keepPlayed: true);
            // Before the loaded entry's first item is handed out nothing in it has
            // played, so it can reorder freely.
            if (_itemsLoaded)
                _itemOrder.SetShuffle(enabled, keepCurrent: _itemHandedOut, keepPlayed: true);
            _ahead?.Order?.SetShuffle(enabled, keepCurrent: false);
        }
    }

    /// <summary>
    /// The track <see cref="NextAsync"/> hands out next, without moving on: the next one in the
    /// loaded entry, else the first of the next entry, which this loads now (and
    /// <see cref="NextAsync"/> then takes, rather than loading it again). Null before the first
    /// track, and at the end of a lap, whose next order isn't drawn until it starts.
    /// </summary>
    public async Task<PlayableItem?> PeekNextAsync(CancellationToken ct)
    {
        var entries = _mix.Entries;
        Ahead ahead;
        lock (_lock)
        {
            if (!_initialized || !_itemsLoaded) return null;
            var rest = _itemOrder.RemainingIndices();
            var skip = _itemHandedOut ? 1 : 0;
            if (rest.Count > skip) return _items[rest[skip]];

            var upcoming = _entryOrder.RemainingIndices();
            if (upcoming.Count < 2) return null;
            if (_ahead?.Entry != upcoming[1]) _ahead = new Ahead(upcoming[1], LoadAheadAsync(entries[upcoming[1]]));
            ahead = _ahead;
        }

        var items = await ahead.Items.WaitAsync(ct).ConfigureAwait(false);
        lock (_lock)
        {
            if (!ReferenceEquals(_ahead, ahead) || items.Count == 0) return null;
            ahead.Order ??= NewOrder(items.Count);
            return items[ahead.Order.CurrentIndex];
        }
    }

    private async Task<List<PlayableItem>> LoadAheadAsync(ContentRef entry)
    {
        try
        {
            return [.. await _resolver.EnumerateAsync(entry, CancellationToken.None).ConfigureAwait(false)];
        }
        catch (Exception ex)
        {
            Log($"enumerate {entry.SourceId}:{entry.Locator} ahead failed: {ex.Message}");
            return [];
        }
    }

    // Under the lock.
    private PlayOrder NewOrder(int count)
    {
        var order = new PlayOrder();
        order.Reset(count);
        if (_shuffle) order.SetShuffle(true, keepCurrent: false);
        return order;
    }

    /// <summary>
    /// The next track to play, resolving entries lazily. Loops the mix forever, so
    /// it returns null only on cancellation or an entry-less mix — never because the
    /// mix "ended".
    /// </summary>
    public async Task<PlayableItem?> NextAsync(CancellationToken ct)
    {
        var entries = _mix.Entries;
        if (entries.Count == 0) return null;

        while (!ct.IsCancellationRequested)
        {
            ContentRef entryToLoad;
            Ahead? ahead;
            lock (_lock)
            {
                if (!_initialized)
                {
                    _entryOrder.Reset(entries.Count);
                    if (_shuffle) _entryOrder.SetShuffle(true, keepCurrent: false);
                    _initialized = true;
                    _itemsLoaded = false;
                }

                if (_itemsLoaded)
                {
                    // Move past the song that was playing (off the end → next entry).
                    if (_itemHandedOut) _itemOrder.Advance(wrap: false);

                    if (_itemOrder.CurrentIndex >= 0)
                    {
                        _itemHandedOut = true;
                        return _items[_itemOrder.CurrentIndex];
                    }

                    // Current entry exhausted — wrap to the next entry (entry order
                    // reshuffles on wrap for a fresh pass).
                    _entryOrder.Advance(wrap: true);
                    _itemsLoaded = false;
                    _itemHandedOut = false;
                }

                entryToLoad = entries[_entryOrder.CurrentIndex];
                ahead = _ahead?.Entry == _entryOrder.CurrentIndex ? _ahead : null;
            }

            // Resolve outside the lock (a YouTube entry spawns yt-dlp), unless it was loaded ahead.
            List<PlayableItem> resolved;
            try
            {
                resolved = ahead != null
                    ? await ahead.Items.WaitAsync(ct).ConfigureAwait(false)
                    : [.. await _resolver.EnumerateAsync(entryToLoad, ct).ConfigureAwait(false)];
            }
            catch (OperationCanceledException) { return null; }
            catch (Exception ex)
            {
                Log($"enumerate {entryToLoad.SourceId}:{entryToLoad.Locator} failed: {ex.Message}");
                resolved = [];
            }

            var backoff = false;
            bool empty;
            lock (_lock)
            {
                _items = resolved;
                _itemOrder = ahead?.Order is { } drawn && drawn.Count == _items.Count ? drawn : NewOrder(_items.Count);
                if (ReferenceEquals(_ahead, ahead)) _ahead = null;
                _itemsLoaded = true;
                _itemHandedOut = false;
                empty = _items.Count == 0;

                if (empty)
                {
                    _idleEntries++;
                    if (_idleEntries >= entries.Count) { _idleEntries = 0; backoff = true; }
                    _entryOrder.Advance(wrap: true);
                    _itemsLoaded = false;
                }
                else
                {
                    _idleEntries = 0;
                }
            }

            if (empty)
            {
                if (backoff)
                {
                    try { await Task.Delay(IdleBackoff, ct).ConfigureAwait(false); }
                    catch (OperationCanceledException) { return null; }
                }
                continue;
            }

            // Items loaded; loop once more to return the first one.
        }

        return null;
    }

    /// <summary>
    /// A best-effort rolling window of what's upcoming, without consuming it: the
    /// already-resolved remaining items of the current entry (exact metadata),
    /// followed by one placeholder per upcoming entry (resolved lazily when reached).
    /// </summary>
    public IReadOnlyList<QueuePreview> Peek(int count)
    {
        if (count <= 0) return [];
        var result = new List<QueuePreview>();
        lock (_lock)
        {
            if (_itemsLoaded)
            {
                // RemainingIndices()[0] is the playing song once it's been handed out.
                foreach (var idx in _itemOrder.RemainingIndices().Skip(_itemHandedOut ? 1 : 0))
                {
                    if (result.Count >= count) return result;
                    result.Add(QueuePreview.ForTrack(_items[idx].Metadata));
                }
            }

            if (_initialized)
            {
                // RemainingIndices()[0] is the current entry — skip it; the rest are
                // upcoming entries we haven't resolved yet.
                foreach (var entryIdx in _entryOrder.RemainingIndices().Skip(1))
                {
                    if (result.Count >= count) return result;
                    result.Add(QueuePreview.ForEntry(_mix.Entries[entryIdx]));
                }
            }
        }
        return result;
    }
}
