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
/// Threading: the order state is touched on the engine thread via
/// <see cref="NextAsync"/> and read by <see cref="Peek"/> from the UI thread, so
/// both go through <see cref="_lock"/>; the expensive enumerate happens outside it.
/// </summary>
public sealed class MixContextProvider
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

    private bool _shuffle;
    private bool _shufflePending;

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

    private static void Log(string msg) => Debug.WriteLine($"[hzn-mix-ctx] {msg}");

    /// <summary>Turn shuffle on or off; applied to both order levels on the engine
    /// thread at the next <see cref="NextAsync"/> boundary so it never races.</summary>
    public void SetShuffle(bool on)
    {
        lock (_lock) { _shuffle = on; _shufflePending = true; }
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
            lock (_lock)
            {
                if (!_initialized)
                {
                    _entryOrder.Reset(entries.Count);
                    if (_shuffle) _entryOrder.SetShuffle(true, keepCurrent: false);
                    _shufflePending = false;
                    _initialized = true;
                    _itemsLoaded = false;
                }

                ApplyShufflePending();

                if (_itemsLoaded)
                {
                    if (_itemOrder.CurrentIndex >= 0)
                    {
                        var item = _items[_itemOrder.CurrentIndex];
                        _itemOrder.Advance(wrap: false); // off the end → reload next entry
                        return item;
                    }

                    // Current entry exhausted — wrap to the next entry (entry order
                    // reshuffles on wrap for a fresh pass).
                    _entryOrder.Advance(wrap: true);
                    _itemsLoaded = false;
                }

                entryToLoad = entries[_entryOrder.CurrentIndex];
            }

            // Resolve outside the lock (a YouTube entry spawns yt-dlp).
            List<PlayableItem> resolved;
            try
            {
                resolved = [.. await _resolver.EnumerateAsync(entryToLoad, ct).ConfigureAwait(false)];
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
                _itemOrder = new PlayOrder();
                _itemOrder.Reset(_items.Count);
                if (_shuffle) _itemOrder.SetShuffle(true, keepCurrent: false);
                _itemsLoaded = true;
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
                foreach (var idx in _itemOrder.RemainingIndices())
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

    // Apply a pending shuffle toggle to both order levels (called under _lock on
    // the engine thread). keepCurrent pins what's playing and shuffles the rest.
    private void ApplyShufflePending()
    {
        if (!_shufflePending) return;
        _shufflePending = false;
        _entryOrder.SetShuffle(_shuffle, keepCurrent: true);
        _itemOrder.SetShuffle(_shuffle, keepCurrent: true);
    }
}
