using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using HorizonRadio.Core.Audio;
using HorizonRadio.Core.Models;

namespace HorizonRadio.Core.Sources.Queue;

/// <summary>
/// The single playback engine for the global queue — the queue-era successor to
/// MixSource. It plays straight down one queue: the explicit "next in queue" items
/// first, then the infinite mix context that refills the tail forever. When both
/// are empty it parks on a gate (idle/silent) rather than ending, so adding an item
/// or setting a context resumes it without restarting the source.
///
/// Reads/mutates its data through a long-lived <see cref="QueueModel"/> (so the
/// queue survives this engine being torn down for a Spotify takeover and rebuilt on
/// return). Owns the runtime concerns MixSource owned: the PCM pump, the pause gate
/// spanning track boundaries, per-track cancellation for skip/restart, and progress
/// delegation to the active item. Previous walks a played-history stack (Spotify-
/// style) rather than a single playlist's order, since the queue spans both zones.
/// </summary>
public sealed class QueueSource : IAudioSource, ITransportControls, IPlaybackProgress
{
    private readonly QueueModel _model;

    public string Id => "queue";
    public string DisplayName => "Queue";

    public event Action<Track>? TrackChanged;
    public event Action<bool>? PausedChanged;

    private CancellationTokenSource? _stopCts;
    private Task? _runLoop;

    // Per-track cancellation, like the playlist sources: skip/restart/jump cancel
    // just the current track; StopAsync cancels the parent (_stopCts).
    private CancellationTokenSource? _trackCts;

    // Played-history transport (engine thread only). _past holds what played
    // before the current track (most recent last); _future holds tracks we stepped
    // back over, so Next redoes them before pulling anything new.
    private readonly List<QueueItem> _past = new();
    private readonly List<QueueItem> _future = new();
    private QueueItem? _current;
    private QueueItem? _replay;
    private bool _lastFromContext;
    private const int MaxHistory = 200;

    private volatile bool _stepBackwards;
    private volatile bool _restart;

    private volatile bool _shuffle;
    private volatile bool _paused;
    private readonly ManualResetEventSlim _resumeGate = new(initialState: true);

    // Signaled when the queue gains work while the engine is parked idle.
    private readonly ManualResetEventSlim _workGate = new(initialState: false);

    // Park / resume (station channels): the track interrupted by a park, and where in it to
    // pick up when the next StartAsync replays it.
    private QueueItem? _parkedItem;
    private TimeSpan _parkedPosition;
    private volatile PlayableItem? _resumeItem;
    private TimeSpan _resumeAt;
    private volatile bool _pauseLocked;

    // The item currently playing — read by the UI progress poll — plus the cached
    // transport capabilities the UI reads (recomputed on the engine thread).
    private volatile PlayableItem? _activeItem;
    private volatile bool _canNext;
    private volatile bool _canPrev;

    public QueueSource(QueueModel model)
    {
        _model = model;
        _model.WorkAvailable += OnWorkAvailable;
        _model.InterruptRequested += OnInterrupt;
        RecomputeCaps();
    }

    private static void Log(string msg) => Debug.WriteLine($"[hzn-queue] {msg}");

    private void OnWorkAvailable() => _workGate.Set();

    // A jump / context-replace: stop the current track and re-pick going forward.
    private void OnInterrupt()
    {
        _stepBackwards = false;
        _restart = false;
        try { _trackCts?.Cancel(); } catch { }
    }

    // -- IAudioSource --

    /// <summary>True once disposed; a disposed source can't be started again.</summary>
    public bool IsDisposed { get; private set; }

    public Task StartAsync(IPcmSink sink, CancellationToken ct)
    {
        if (_runLoop != null) return Task.CompletedTask;
        ObjectDisposedException.ThrowIf(IsDisposed, this);
        // StopAsync opens the pause gate so a stopped pump can exit; close it again if we're
        // coming back paused.
        if (_paused) _resumeGate.Reset();
        _stopCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        _runLoop = Task.Run(() => RunAsync(sink, _stopCts.Token), _stopCts.Token);
        return Task.CompletedTask;
    }

    public async Task StopAsync()
    {
        _stopCts?.Cancel();
        _trackCts?.Cancel();
        _resumeGate.Set();
        _workGate.Set();
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
        if (IsDisposed) return;
        IsDisposed = true;
        _model.WorkAvailable -= OnWorkAvailable;
        _model.InterruptRequested -= OnInterrupt;
        await StopAsync().ConfigureAwait(false);
        _resumeGate.Dispose();
        _workGate.Dispose();
    }

    // -- ITransportControls --

    public bool CanPause => !_pauseLocked;
    public bool CanSkipNext => _canNext;
    public bool CanSkipPrevious => _canPrev;
    public bool IsPaused => _paused;
    public bool CanShuffle => _model.HasContext;
    public bool IsShuffled => _shuffle;

    public Task SetShuffleAsync(bool enabled)
    {
        _shuffle = enabled;
        _model.Context?.SetShuffle(enabled);
        return Task.CompletedTask;
    }

    /// <summary>
    /// Disable manual pause (a "live radio" station keeps going no matter what). Locking while
    /// paused resumes playback.
    /// </summary>
    public bool PauseLocked
    {
        get => _pauseLocked;
        set
        {
            _pauseLocked = value;
            if (value && _paused) _ = TogglePauseAsync(force: true);
        }
    }

    public Task TogglePauseAsync() => TogglePauseAsync(force: false);

    private Task TogglePauseAsync(bool force)
    {
        if (_pauseLocked && !force && !_paused) return Task.CompletedTask;
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
        _restart = true;
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

    // -- Park / resume --

    /// <summary>
    /// Remember the playing track and position, ahead of the owner parking this source
    /// (<see cref="SourceRunner.ParkAsync"/>). Call while it's still running.
    /// </summary>
    public void MarkParked()
    {
        _parkedItem = _current;
        _parkedPosition = _activeItem?.Position ?? TimeSpan.Zero;
    }

    /// <summary>
    /// Before restarting a parked source: continue the interrupted track instead of moving on,
    /// at its parked position plus <paramref name="advanceBy"/> ("live radio": time kept passing
    /// while we were away). If that lands past the track's known end, the source moves on to the
    /// next track instead. Items that can't seek restart the track from its beginning.
    /// </summary>
    public void ResumeFromPark(TimeSpan advanceBy)
    {
        var parked = _parkedItem;
        _parkedItem = null;
        if (parked == null) return;

        var target = _parkedPosition + (advanceBy > TimeSpan.Zero ? advanceBy : TimeSpan.Zero);
        if (parked.Item.Duration is { } duration && target >= duration)
            return; // finished while away: the forward pick moves on to the next track

        _replay = parked;
        _resumeItem = parked.Item;
        _resumeAt = target;
    }

    // -- Run loop --

    private async Task RunAsync(IPcmSink sink, CancellationToken ct)
    {
        var pumpCtx = new PumpContext
        {
            Sink = sink,
            IsPaused = () => _paused,
            ResumeGate = _resumeGate,
            StartPosition = item =>
            {
                // Only the replayed, parked track resumes mid-way; and only once.
                if (!ReferenceEquals(_resumeItem, item)) return TimeSpan.Zero;
                _resumeItem = null;
                return _resumeAt;
            },
            OnStarted = item =>
            {
                _activeItem = item;
                TrackChanged?.Invoke(item.Metadata);
                // Republish now-playing once metadata is final (a remote item
                // refines its title/art during prepare).
                if (_current != null) _model.SetNowPlaying(_current, _lastFromContext);
            },
            OnMetadataUpdated = item =>
            {
                // A live item (radio) changed song mid-play: republish the HUD track
                // and now-playing so enrichment re-runs for the new song. Guard on the
                // item still being active so a late callback from a skipped item is ignored.
                if (!ReferenceEquals(_activeItem, item)) return;
                TrackChanged?.Invoke(item.Metadata);
                if (_current != null) _model.SetNowPlaying(_current, _lastFromContext);
            },
        };

        while (!ct.IsCancellationRequested)
        {
            var next = await PickNextAsync(ct).ConfigureAwait(false);

            if (next == null)
            {
                if (ct.IsCancellationRequested) break;

                // Idle: nothing to play. Park on the work gate until an item is
                // queued or a context is set. Re-check after Reset so an add that
                // raced the Reset isn't lost.
                _current = null;
                _model.SetNowPlaying(null, false);
                RecomputeCaps();
                _workGate.Reset();
                if (_model.HasWork) continue;
                try { _workGate.Wait(ct); }
                catch (OperationCanceledException) { break; }
                continue;
            }

            _current = next;
            _activeItem = next.Item;
            _model.SetNowPlaying(next, _lastFromContext);
            RecomputeCaps();

            using var trackCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            _trackCts = trackCts;
            _restart = false;
            _stepBackwards = false;

            try
            {
                await next.Item.PlayAsync(pumpCtx, trackCts.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                return;
            }
            catch (OperationCanceledException)
            {
                // Per-track skip/restart/jump — the next pick handles the intent.
            }
            catch (Exception ex)
            {
                Log($"item failed: {ex.GetType().Name}: {ex.Message}");
            }

            if (ReferenceEquals(_trackCts, trackCts)) _trackCts = null;

            // A restart replays the same track without disturbing history.
            if (_restart) { _replay = next; _restart = false; }
        }
    }

    /// <summary>Decide the next track: a pending restart, a step back through
    /// history, a redo, then the explicit zone, then the context. Updates
    /// <see cref="_current"/>/<see cref="_lastFromContext"/> and history.</summary>
    private async Task<QueueItem?> PickNextAsync(CancellationToken ct)
    {
        if (_replay != null)
        {
            var r = _replay;
            _replay = null;
            return r; // keeps _current / _lastFromContext as-is
        }

        if (_stepBackwards)
        {
            _stepBackwards = false;
            if (_past.Count > 0)
            {
                if (_current != null) _future.Add(_current);
                var prev = _past[^1];
                _past.RemoveAt(_past.Count - 1);
                _lastFromContext = prev.FromContext;
                return prev;
            }
            // Nothing behind — replay the current track if there is one.
            if (_current != null) return _current;
        }

        // Forward: the just-finished track moves into history.
        if (_current != null)
        {
            _past.Add(_current);
            if (_past.Count > MaxHistory) _past.RemoveAt(0);
        }

        if (_future.Count > 0)
        {
            var f = _future[^1];
            _future.RemoveAt(_future.Count - 1);
            _lastFromContext = f.FromContext;
            return f;
        }

        var explicitItem = _model.TakeExplicitFront();
        if (explicitItem != null)
        {
            _lastFromContext = false;
            return explicitItem;
        }

        var context = _model.Context;
        if (context != null)
        {
            // Show a placeholder while the first/next context entry resolves (a
            // YouTube enumerate takes a beat) so the HUD isn't blank.
            if (_current == null)
                TrackChanged?.Invoke(new Track(
                    Title: "Loading…", Artist: context.DisplayName, Album: null, AlbumArt: null,
                    SourceId: Id, SourceDisplay: context.DisplayName, ExternalId: null));

            var item = await context.NextAsync(ct).ConfigureAwait(false);
            if (item != null)
            {
                _lastFromContext = true;
                return new QueueItem(item, fromContext: true);
            }
        }

        return null;
    }

    private void RecomputeCaps()
    {
        _canPrev = _past.Count > 0;
        _canNext = _future.Count > 0 || _model.HasWork;
    }
}
