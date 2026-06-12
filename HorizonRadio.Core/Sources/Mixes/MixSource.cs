using System.Diagnostics;
using HorizonRadio.Core.Audio;
using HorizonRadio.Core.Models;

namespace HorizonRadio.Core.Sources.Mixes;

/// <summary>
/// Plays a <see cref="Mix"/> — an ordered list of cross-source entries — as one
/// continuous radio stream. It owns the ordering itself (two <see cref="PlayOrder"/>
/// levels: entries, and the items within the current entry) and drives the leaf
/// <see cref="PlayableItem"/>s, so transport and shuffle live in one place rather
/// than being delegated to per-source black boxes.
///
/// Shuffle is two-level and "grouped": the entries shuffle among themselves and
/// each collection entry's items shuffle within it, but an entry stays a unit.
/// The whole mix loops continuously (the entry order wraps and reshuffles), which
/// is what a radio replacement wants.
/// </summary>
public sealed class MixSource : IAudioSource, ITransportControls, IPlaybackProgress
{
    private readonly Mix _mix;
    private readonly MixContentResolver _resolver;

    public string Id => "mix";
    public string DisplayName => _mix.Name;

    public event Action<Track>? TrackChanged;
    public event Action<bool>? PausedChanged;

    // Entry-level order (over _mix.Entries) and the item-level order for the
    // entry currently playing. Both touched only on the run-loop thread.
    private readonly PlayOrder _entryOrder = new();
    private PlayOrder _itemOrder = new();
    private List<PlayableItem> _items = new();

    private CancellationTokenSource? _stopCts;
    private Task? _runLoop;

    // Per-item cancellation, like the other playlist sources: skip/restart cancel
    // just the current item; StopAsync cancels the parent (_stopCts).
    private CancellationTokenSource? _trackCts;
    private volatile bool _stepBackwards;
    private volatile bool _restartCurrent;

    // Desired shuffle state + a pending flag applied on the run-loop thread so a
    // mid-playback toggle never races the orders.
    private volatile bool _shuffle;
    private volatile bool _shufflePending;

    // The item currently playing — read by the UI progress poll.
    private volatile PlayableItem? _activeItem;

    // Set when an item actually enters playback (its OnStarted fired), so the run
    // loop can tell a productive pass from one where every entry was empty or
    // every item failed to resolve. Touched only on the run-loop thread.
    private bool _itemStarted;

    private volatile bool _paused;
    private readonly ManualResetEventSlim _resumeGate = new(initialState: true);

    public MixSource(Mix mix, MixContentResolver resolver)
    {
        _mix = mix;
        _resolver = resolver;
    }

    private static void Log(string msg) => Debug.WriteLine($"[hzn-mix] {msg}");

    // -- IAudioSource --

    public Task StartAsync(IPcmSink sink, CancellationToken ct)
    {
        if (_runLoop != null) return Task.CompletedTask;
        _stopCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        _runLoop = Task.Run(() => RunAsync(sink, _stopCts.Token), _stopCts.Token);
        return Task.CompletedTask;
    }

    public async Task StopAsync()
    {
        _stopCts?.Cancel();
        _trackCts?.Cancel();
        _resumeGate.Set();
        if (_runLoop != null)
        {
            try { await _runLoop.ConfigureAwait(false); }
            catch { }
            _runLoop = null;
        }

        _stopCts?.Dispose();
        _stopCts = null;
        _trackCts?.Dispose();
        _trackCts = null;
        _activeItem = null;
    }

    public async ValueTask DisposeAsync()
    {
        await StopAsync().ConfigureAwait(false);
        _resumeGate.Dispose();
    }

    // -- ITransportControls --

    public bool CanPause => true;
    public bool CanSkipNext => _mix.Entries.Count > 0;
    public bool CanSkipPrevious => _mix.Entries.Count > 0;
    public bool IsPaused => _paused;
    public bool CanShuffle => _mix.Entries.Count > 0;
    public bool IsShuffled => _shuffle;

    public Task SetShuffleAsync(bool enabled)
    {
        _shuffle = enabled;
        _shufflePending = true;
        return Task.CompletedTask;
    }

    public Task TogglePauseAsync()
    {
        _paused = !_paused;
        if (_paused) _resumeGate.Reset();
        else _resumeGate.Set();
        PausedChanged?.Invoke(_paused);
        return Task.CompletedTask;
    }

    public Task NextAsync()
    {
        _stepBackwards = false;
        _trackCts?.Cancel();
        return Task.CompletedTask;
    }

    public Task PreviousAsync()
    {
        _stepBackwards = true;
        _trackCts?.Cancel();
        return Task.CompletedTask;
    }

    public Task RestartAsync()
    {
        _restartCurrent = true;
        _trackCts?.Cancel();
        return Task.CompletedTask;
    }

    // -- IPlaybackProgress (delegated to the active item) --

    public TimeSpan? Duration => _activeItem?.Duration;
    public TimeSpan Position => _activeItem?.Position ?? TimeSpan.Zero;
    public bool CanSeek => _activeItem?.CanSeek ?? false;

    public Task SeekAsync(TimeSpan position)
    {
        _activeItem?.Seek(position);
        return Task.CompletedTask;
    }

    // -- Run loop --

    private async Task RunAsync(IPcmSink sink, CancellationToken ct)
    {
        var entries = _mix.Entries;
        if (entries.Count == 0) { Log("mix has no entries; idle"); return; }

        // Placeholder until the first item resolves (a YouTube entry's resolve
        // takes a beat) so the HUD shows the mix immediately.
        TrackChanged?.Invoke(new Track(
            Title: "Loading…", Artist: _mix.Name, Album: null, AlbumArt: null,
            SourceId: Id, SourceDisplay: _mix.Name, ExternalId: null));

        var pumpCtx = new PumpContext
        {
            Sink = sink,
            IsPaused = () => _paused,
            ResumeGate = _resumeGate,
            OnStarted = item =>
            {
                _itemStarted = true;
                _activeItem = item;
                TrackChanged?.Invoke(item.Metadata);
            },
        };

        _entryOrder.Reset(entries.Count);
        if (_shuffle) _entryOrder.SetShuffle(true, keepCurrent: false);
        _shufflePending = false;

        // Entries traversed since anything last played. When it reaches a full
        // lap of the mix with nothing played (all entries empty / every item
        // failed to resolve), back off instead of spinning the CPU and
        // re-spawning yt-dlp in a tight loop.
        var idleEntries = 0;

        while (!ct.IsCancellationRequested && _entryOrder.CurrentIndex >= 0)
        {
            var entry = entries[_entryOrder.CurrentIndex];
            var entryPlayed = false;

            try
            {
                _items = [.. await _resolver.EnumerateAsync(entry, ct).ConfigureAwait(false)];
            }
            catch (OperationCanceledException) { return; }
            catch (Exception ex)
            {
                Log($"enumerate {entry.SourceId}:{entry.Locator} failed: {ex.Message}");
                _items = [];
            }

            if (_items.Count > 0)
            {
                _itemOrder = new PlayOrder();
                _itemOrder.Reset(_items.Count);
                if (_shuffle) _itemOrder.SetShuffle(true, keepCurrent: false);

                while (!ct.IsCancellationRequested && _itemOrder.CurrentIndex >= 0)
                {
                    using var trackCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                    _trackCts = trackCts;
                    _stepBackwards = false;
                    _itemStarted = false;

                    var item = _items[_itemOrder.CurrentIndex];
                    _activeItem = item;

                    try
                    {
                        await item.PlayAsync(pumpCtx, trackCts.Token).ConfigureAwait(false);
                    }
                    catch (OperationCanceledException) when (ct.IsCancellationRequested)
                    {
                        return;
                    }
                    catch (OperationCanceledException)
                    {
                        // Per-item skip; the order advances below.
                    }
                    catch (Exception ex)
                    {
                        Log($"item failed: {ex.GetType().Name}: {ex.Message}");
                    }

                    if (_itemStarted) entryPlayed = true;
                    if (ReferenceEquals(_trackCts, trackCts)) _trackCts = null;

                    ApplyShufflePending(keepCurrent: true);

                    if (_restartCurrent) _restartCurrent = false;          // replay item
                    else if (_stepBackwards) _itemOrder.Retreat(wrap: false); // clamp at entry start
                    else _itemOrder.Advance(wrap: false);                  // off end → next entry
                }
            }

            ApplyShufflePending(keepCurrent: true);

            idleEntries = entryPlayed ? 0 : idleEntries + 1;
            if (idleEntries >= entries.Count)
            {
                idleEntries = 0;
                try { await Task.Delay(IdleBackoff, ct).ConfigureAwait(false); }
                catch (OperationCanceledException) { return; }
            }

            // Wrap at the entry level so the mix loops continuously (and a
            // shuffled entry order reshuffles for a fresh pass).
            _entryOrder.Advance(wrap: true);
        }
    }

    /// <summary>How long to pause after a full lap of the mix produced no audio,
    /// to avoid a tight retry loop when everything is unplayable.</summary>
    private static readonly TimeSpan IdleBackoff = TimeSpan.FromSeconds(3);

    // Apply a pending shuffle toggle to both order levels on the run-loop thread.
    // keepCurrent pins what's playing and shuffles the rest around it.
    private void ApplyShufflePending(bool keepCurrent)
    {
        if (!_shufflePending) return;
        _shufflePending = false;
        _entryOrder.SetShuffle(_shuffle, keepCurrent);
        _itemOrder.SetShuffle(_shuffle, keepCurrent);
    }
}
