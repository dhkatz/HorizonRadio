using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using HorizonRadio.Core.Models;

namespace HorizonRadio.Core.Sources.Queue;

/// <summary>
/// The single playback engine for the global queue — the queue-era successor to
/// MixSource. It plays straight down one queue: the explicit "next in queue" items
/// first, then the infinite mix context that refills the tail forever. When both
/// are empty it waits (idle/silent) rather than ending, so adding an item or setting
/// a context resumes it without restarting the source.
///
/// Reads/mutates its data through a long-lived <see cref="QueueModel"/> (so the
/// queue survives this engine being torn down for a Spotify takeover and rebuilt on
/// return). Previous walks a played-history stack (Spotify-style) rather than a single
/// playlist's order, since the queue spans both zones.
///
/// <para>The engine is one state machine (<see cref="Phase"/>), owned by its run loop and
/// guarded by one lock (<see cref="_gate"/>):</para>
/// <code>
///   Parked ──start──► Playing(item, at)               resume where it stopped
///          └────────► Picking                         nothing to resume, or asked to move on
///   Picking ─found──► Playing(item, 0)                or straight on, if a skip came meanwhile
///           ─none───► Idle ──work / request──► Picking
///   Playing ─ended / request──► Playing(next)         history, redo, restart
///                            └► Picking               the next one comes from the queue
///   any ─────stop───► Parked(item, where it was)
/// </code>
/// <para>Everything else (transport, play-now, a mix switch) only <em>asks</em>: it records a
/// request and stops the step under way, and the loop acts on it between steps. So a request
/// is never lost or applied twice, whichever thread it comes from and whenever it lands.</para>
/// </summary>
public sealed class QueueSource : IAudioSource, ITransportControls, IPlaybackProgress
{
    private const int MaxHistory = 200;

    private readonly QueueModel _model;

    public string Id => "queue";
    public string DisplayName => "Queue";

    public event Action<Track>? TrackChanged;
    public event Action<bool>? PausedChanged;

    // What the engine has been asked to do, merged: a jump wins (the queue changed under the
    // track), otherwise the latest request does.
    private enum Intent { Forward, Back, Replay, Jump }

    private abstract record Phase;

    /// <summary>Nothing to play; waits for the queue to get something, or for a request.</summary>
    private sealed record Idle : Phase;

    /// <summary>Getting the next track from the queue: an explicit item, else the context, which
    /// can take a while to resolve. The track before it, if any, is already in history.</summary>
    private sealed record Picking : Phase;

    /// <summary>Playing <paramref name="Item"/> from <paramref name="StartAt"/> (non-zero only
    /// when resuming a park).</summary>
    private sealed record Playing(QueueItem Item, TimeSpan StartAt) : Phase
    {
        /// <summary>The item reported it has begun (so its position means something).</summary>
        public bool Started { get; set; }

        /// <summary>Where it was the moment a stop was asked for, before it wound down.</summary>
        public TimeSpan? StoppedAt { get; set; }
    }

    /// <summary>Not running. Starting again plays <paramref name="Item"/> from
    /// <paramref name="At"/>, or moves on from it when something was asked for meanwhile or it
    /// would have ended by then.</summary>
    private sealed record Parked(QueueItem? Item, TimeSpan At) : Phase;

    private sealed record Run(Task Loop, CancellationTokenSource Stop);

    // A step's result: whether it ran to its own end (a track that finished, a pick that
    // completed) rather than being cut short, and what a pick found.
    private readonly record struct Outcome(bool Completed, QueueItem? Found = null);

    private static readonly Phase IdlePhase = new Idle();
    private static readonly Phase PickingPhase = new Picking();

    private volatile bool _shuffle;

    // ---- Everything below is guarded by _gate. Only the run loop moves _phase on (besides
    // ResumeFromPark nudging a Parked one before a restart). ----
    private readonly Lock _gate = new();
    private Phase _phase = new Parked(null, TimeSpan.Zero);

    // Requests not yet acted on: the latest transport step, and the newest model generation a
    // jump asked for (pending while it's newer than _basis).
    private Intent? _request;
    private int _jumpTo;

    // The model generation at the engine's latest read of the queue: the pick under way, or the
    // one that chose what's on. A jump at or before it is already reflected there.
    private int _basis;

    // Cancels the step under way — a pick, a play, or an idle wait — so a request takes effect
    // now. Cancelled outside the lock (it runs the item's teardown), hence TryCancel.
    private CancellationTokenSource? _step;
    private Run? _run;

    // Played history: _past holds what played before the current track (most recent last);
    // _future the tracks Previous stepped back over, which Next redoes before anything new.
    private readonly List<QueueItem> _past = new();
    private readonly List<QueueItem> _future = new();

    // Pause spans track boundaries. Written under the lock together with the gate, so the gate
    // is open exactly when not paused; volatile for the pump's per-chunk read.
    private volatile bool _paused;
    private bool _pauseLocked;
    private readonly ManualResetEventSlim _resumeGate = new(initialState: true);

    // Completed when the queue gains work, to wake an idle engine.
    private TaskCompletionSource _work = NewSignal();

    public QueueSource(QueueModel model)
    {
        _model = model;
        _model.WorkAvailable += OnWorkAvailable;
        _model.InterruptRequested += OnInterrupt;
    }

    private static void Log(string msg) => Trace.WriteLine($"[hzn-queue] {msg}");

    private static TaskCompletionSource NewSignal() => new(TaskCreationOptions.RunContinuationsAsynchronously);

    // A step's cancellation source is disposed by the run loop on its own thread, so one read
    // under the lock can be gone by the time it's cancelled: the step it stopped is over then.
    private static void TryCancel(CancellationTokenSource? cts)
    {
        if (cts == null) return;
        try { cts.Cancel(); }
        catch (ObjectDisposedException) { }
    }

    // -- Requests --

    private void Request(Intent intent)
    {
        CancellationTokenSource? step;
        lock (_gate)
        {
            _request = intent;
            // A skip or restart asked for while the next track loads applies to that track when
            // it arrives (so its resolve isn't thrown away); anything else stops the step now.
            if (_phase is Picking && intent is Intent.Forward or Intent.Replay) return;
            step = _step;
        }
        TryCancel(step);
    }

    // A play-now jump, or a mix replace that should take effect now.
    private void OnInterrupt()
    {
        var generation = _model.Generation;
        CancellationTokenSource? step;
        lock (_gate)
        {
            if (generation <= _basis) return; // what's on now was already picked after the change
            _jumpTo = Math.Max(_jumpTo, generation);
            step = _step;
        }
        TryCancel(step);
    }

    private void OnWorkAvailable()
    {
        lock (_gate) _work.TrySetResult();
    }

    // Under the lock.
    private Intent? PendingRequest() => _jumpTo > _basis ? Intent.Jump : _request;

    // Under the lock.
    private Intent? TakeRequest()
    {
        var asked = PendingRequest();
        _request = null;
        _jumpTo = 0;
        return asked;
    }

    // -- IAudioSource --

    /// <summary>True once disposed; a disposed source can't be started again.</summary>
    public bool IsDisposed { get; private set; }

    public Task StartAsync(IPcmSink sink, CancellationToken ct)
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(IsDisposed, this);
            if (_run is { Loop.IsCompleted: false }) return Task.CompletedTask;
            _run?.Stop.Dispose();
            var stop = CancellationTokenSource.CreateLinkedTokenSource(ct);
            _run = new Run(Task.Run(() => RunAsync(sink, stop.Token), CancellationToken.None), stop);
        }
        return Task.CompletedTask;
    }

    public async Task StopAsync()
    {
        Run? run;
        lock (_gate) run = _run;
        if (run == null) return;
        TryCancel(run.Stop);
        try
        {
            await run.Loop.ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            Log($"run loop failed: {ex.GetType().Name}: {ex.Message}");
        }
        finally
        {
            // Always forget the run, or the next StartAsync would think it's still going.
            lock (_gate)
            {
                if (ReferenceEquals(_run, run))
                {
                    _run = null;
                    run.Stop.Dispose();
                }
            }
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (IsDisposed) return;
        IsDisposed = true;
        _model.WorkAvailable -= OnWorkAvailable;
        _model.InterruptRequested -= OnInterrupt;
        await StopAsync().ConfigureAwait(false);
        // Nothing will play from this engine again, so the queue shows nothing playing. (A
        // parked engine keeps its track there: it comes back to it.)
        _model.SetNowPlaying(null, false);
        lock (_gate) _resumeGate.Dispose();
    }

    // -- ITransportControls --

    public bool CanPause
    {
        get { lock (_gate) return !_pauseLocked; }
    }

    // Live, so adding to the queue enables Next straight away.
    public bool CanSkipNext
    {
        get
        {
            lock (_gate)
            {
                if (_future.Count > 0) return true;
            }
            return _model.HasWork;
        }
    }

    public bool CanSkipPrevious
    {
        get { lock (_gate) return _past.Count > 0; }
    }

    public bool IsPaused => _paused;
    public bool CanShuffle => _model.HasContext;
    public bool IsShuffled => _shuffle;

    public Task SetShuffleAsync(bool enabled)
    {
        _shuffle = enabled;
        _model.SetShuffle(enabled);
        return Task.CompletedTask;
    }

    /// <summary>
    /// Disable manual pause (a "live radio" station keeps going no matter what). Locking while
    /// paused resumes playback.
    /// </summary>
    public bool PauseLocked
    {
        get { lock (_gate) return _pauseLocked; }
        set
        {
            bool resumed;
            lock (_gate)
            {
                _pauseLocked = value;
                resumed = value && ApplyPaused(false);
            }
            if (resumed) PausedChanged?.Invoke(_paused);
        }
    }

    public Task TogglePauseAsync()
    {
        bool changed;
        lock (_gate) changed = ApplyPaused(!_paused);
        // Raise the state as it is now, not as this call set it: with toggles on several threads
        // the last event raised then always carries the final state.
        if (changed) PausedChanged?.Invoke(_paused);
        return Task.CompletedTask;
    }

    public Task SetPausedAsync(bool paused)
    {
        bool changed;
        lock (_gate) changed = ApplyPaused(paused);
        if (changed) PausedChanged?.Invoke(_paused);
        return Task.CompletedTask;
    }

    // Under the lock. False when nothing changed: already so, or pausing is locked.
    private bool ApplyPaused(bool paused)
    {
        if (IsDisposed || paused == _paused || (paused && _pauseLocked)) return false;
        _paused = paused;
        if (paused) _resumeGate.Reset();
        else _resumeGate.Set();
        return true;
    }

    public Task NextAsync()
    {
        Request(Intent.Forward);
        return Task.CompletedTask;
    }

    public Task PreviousAsync()
    {
        Request(Intent.Back);
        return Task.CompletedTask;
    }

    public Task RestartAsync()
    {
        Request(Intent.Replay);
        return Task.CompletedTask;
    }

    // -- IPlaybackProgress (delegated to the item playing) --

    private PlayableItem? ItemPlaying
    {
        get { lock (_gate) return _phase is Playing playing ? playing.Item.Item : null; }
    }

    /// <summary>The item being played, and whether it has begun (so its position means
    /// something); null between tracks, while the next is picked. Unlike the model's
    /// <see cref="QueueModel.Current"/>, which shows the last track until the next one is on.</summary>
    public (QueueItem Item, bool Started)? PlayingNow
    {
        get { lock (_gate) return _phase is Playing playing ? (playing.Item, playing.Started) : null; }
    }

    public TimeSpan? Duration => ItemPlaying?.Duration;
    public TimeSpan Position => ItemPlaying?.Position ?? TimeSpan.Zero;
    public TimeSpan? SongPosition => ItemPlaying?.SongPosition;
    public ILiveSongAudio? LiveAudio => ItemPlaying as ILiveSongAudio;
    public bool CanSeek => ItemPlaying?.CanSeek ?? false;

    public Task SeekAsync(TimeSpan position)
    {
        ItemPlaying?.Seek(position);
        return Task.CompletedTask;
    }

    // -- Park / resume --

    /// <summary>
    /// Before restarting a parked source: let <paramref name="advanceBy"/> pass first ("live
    /// radio": time kept passing while we were away), so the parked track picks up that much
    /// later, or, past its known end, the source moves on to the next track. A restart without
    /// this continues the track where it stopped. Items that can't seek start it from the top.
    /// </summary>
    public void ResumeFromPark(TimeSpan advanceBy)
    {
        if (advanceBy <= TimeSpan.Zero) return;
        lock (_gate)
        {
            if (_phase is Parked { Item: not null } parked)
                _phase = parked with { At = parked.At + advanceBy };
        }
    }

    private static bool Finished(Parked parked) =>
        parked.Item?.Item.Duration is { } duration && parked.At >= duration;

    // Runs on the stopping thread the moment a stop is asked for, before the track winds down
    // (an item can forget its position once it stops): note where it was, then stop the step.
    private void OnStopRequested()
    {
        CancellationTokenSource? step;
        lock (_gate)
        {
            if (_phase is Playing playing) playing.StoppedAt = WhereNow(playing);
            step = _step;
        }
        TryCancel(step);
    }

    private static TimeSpan WhereNow(Playing playing) =>
        playing.Started && playing.Item.Item.Position is { Ticks: > 0 } position ? position : playing.StartAt;

    // -- The state machine --

    private async Task RunAsync(IPcmSink sink, CancellationToken stop)
    {
        using var onStop = stop.Register(OnStopRequested);
        var outcome = new Outcome(Completed: false);
        try
        {
            while (true)
            {
                Phase phase;
                CancellationToken step;
                lock (_gate)
                {
                    if (stop.IsCancellationRequested)
                    {
                        // A step that ran to its own end (a track that finished, a pick that found
                        // something) is taken in first; one the stop cut short parks as it was.
                        if (outcome.Completed) _phase = After(_phase, outcome);
                        _phase = Park(_phase);
                        return;
                    }
                    phase = _phase = After(_phase, outcome);
                    _step?.Dispose();
                    _step = new CancellationTokenSource();
                    step = _step.Token;
                }

                Announce(phase);
                outcome = phase switch
                {
                    Playing playing => await PlayAsync(playing, sink, step).ConfigureAwait(false),
                    Picking => await PickAsync(step).ConfigureAwait(false),
                    _ => await IdleAsync(step).ConfigureAwait(false),
                };
            }
        }
        finally
        {
            lock (_gate)
            {
                // Parked already, unless something threw: either way, restartable.
                _phase = Park(_phase);
                _step?.Dispose();
                _step = null;
            }
        }
    }

    // Under the lock: where the engine goes after a step, acting on what was asked meanwhile.
    private Phase After(Phase from, Outcome outcome)
    {
        switch (from)
        {
            case Parked parked:
                {
                    var asked = TakeRequest();
                    if (asked is null && parked.Item is { } item && !Finished(parked))
                        return new Playing(item, parked.At);
                    return Leave(parked.Item, asked ?? Intent.Forward);
                }

            case Playing playing:
                return Leave(playing.Item, TakeRequest() ?? Intent.Forward);

            case Picking when outcome is { Completed: true, Found: { } found }
                && !(found.FromContext && _model.Generation != _basis):
                {
                    // A skip asked for while it loaded skips it; a restart is moot.
                    var asked = TakeRequest();
                    return asked is null or Intent.Replay ? new Playing(found, TimeSpan.Zero) : Leave(found, asked.Value);
                }

            case Picking when outcome is { Completed: true, Found: null }:
                {
                    // Nothing to play. Back and a jump still mean something; a skip or restart doesn't.
                    var asked = TakeRequest();
                    return asked is Intent.Back or Intent.Jump ? Leave(null, asked.Value) : IdlePhase;
                }

            case Picking:
                // Cut short, or it found a track from a context the queue has since left: pick again.
                // A skip or restart asked for meanwhile still applies to what that brings.
                return PendingRequest() is Intent.Back or Intent.Jump ? Leave(null, TakeRequest()!.Value) : PickingPhase;

            default: // Idle: work came in, or a request did
                return Leave(null, TakeRequest() ?? Intent.Forward);
        }
    }

    // Under the lock: leave `current` (null when nothing is on) for `why`, keeping history, and
    // return what comes next — Picking when it has to come from the queue.
    private Phase Leave(QueueItem? current, Intent why)
    {
        switch (why)
        {
            case Intent.Replay when current != null:
                return new Playing(current, TimeSpan.Zero);

            case Intent.Back when _past.Count > 0:
                if (current != null) _future.Add(current);
                var previous = _past[^1];
                _past.RemoveAt(_past.Count - 1);
                return new Playing(previous, TimeSpan.Zero);

            case Intent.Back when current != null:
                return new Playing(current, TimeSpan.Zero); // nothing behind: replay it
        }

        // Forward (or a jump): the track moves into history.
        if (current != null)
        {
            _past.Add(current);
            if (_past.Count > MaxHistory) _past.RemoveAt(0);
        }

        if (why == Intent.Jump)
        {
            // The queue changed: what we stepped back over no longer comes next.
            _future.Clear();
        }
        else if (_future.Count > 0)
        {
            var redo = _future[^1];
            _future.RemoveAt(_future.Count - 1);
            return new Playing(redo, TimeSpan.Zero);
        }

        return PickingPhase;
    }

    // Under the lock.
    private static Parked Park(Phase phase) => phase switch
    {
        Parked parked => parked,
        Playing playing => new Parked(playing.Item, playing.StoppedAt ?? WhereNow(playing)),
        // Picking or idle: the track before is already in history, so starting again picks on.
        _ => new Parked(null, TimeSpan.Zero),
    };

    // Publish a step the loop just moved into.
    private void Announce(Phase phase)
    {
        switch (phase)
        {
            case Playing playing:
                _model.SetNowPlaying(playing.Item, playing.Item.FromContext);
                break;
            case Idle:
                _model.SetNowPlaying(null, false);
                break;
        }
    }

    private async Task<Outcome> PlayAsync(Playing playing, IPcmSink sink, CancellationToken step)
    {
        var pump = new PumpContext
        {
            Sink = sink,
            IsPaused = () => _paused,
            ResumeGate = _resumeGate,
            // Only this play starts mid-way, and only where it was asked to (resuming a park, or
            // the first play of an item joining one under way elsewhere): a restart or a step back
            // onto the same item is a new play from the top.
            StartPosition = _ => playing.StartAt > TimeSpan.Zero ? playing.StartAt : playing.Item.TakeJoinAt(),
            OnStarted = item => OnItemUpdated(playing, item, started: true),
            // A live item (radio) changed song mid-play: republish so enrichment re-runs.
            OnMetadataUpdated = item => OnItemUpdated(playing, item, started: false),
        };

        try
        {
            await playing.Item.Item.PlayAsync(pump, step).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // A request or a stop; the loop acts on it.
        }
        catch (Exception ex)
        {
            Log($"item failed: {ex.GetType().Name}: {ex.Message}");
        }
        return new Outcome(Completed: !step.IsCancellationRequested);
    }

    // The playing item began, or its metadata changed: republish the HUD track and now-playing
    // (a remote item refines its title and art while it prepares). Ignored once the engine has
    // moved on, so a late callback from a skipped play can't show the wrong track.
    private void OnItemUpdated(Playing playing, PlayableItem item, bool started)
    {
        lock (_gate)
        {
            if (!ReferenceEquals(_phase, playing)) return;
            if (started) playing.Started = true;
        }
        TrackChanged?.Invoke(item.Metadata);
        _model.SetNowPlaying(playing.Item, playing.Item.FromContext);
    }

    private async Task<Outcome> PickAsync(CancellationToken step)
    {
        QueueItem? item;
        IQueueContext? context;
        lock (_gate)
        {
            (item, context, _basis) = _model.TakeNext();
        }

        if (item != null)
        {
            _model.NotifyChanged(); // it left the explicit list
            return new Outcome(Completed: true, item);
        }
        if (context is not { HasEntries: true }) return new Outcome(Completed: true);

        // Show a placeholder while the context's next entry resolves (a YouTube enumerate takes a
        // beat) when nothing was on before, so the HUD isn't blank. It isn't a song, so it's
        // never looked up.
        if (_model.Current == null)
            TrackChanged?.Invoke(new Track(
                Title: "Loading…", Artist: context.DisplayName, Album: null, AlbumArt: null,
                SourceId: Id, SourceDisplay: context.DisplayName, ExternalId: null,
                Resolvable: false));

        try
        {
            var next = await context.NextAsync(step).ConfigureAwait(false);
            if (next != null) return new Outcome(Completed: true, new QueueItem(next, fromContext: true));
        }
        catch (OperationCanceledException) when (step.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            Log($"context failed: {ex.GetType().Name}: {ex.Message}");
            // Don't spin on a context that keeps failing.
            try { await Task.Delay(TimeSpan.FromSeconds(1), step).ConfigureAwait(false); }
            catch (OperationCanceledException) { }
            return new Outcome(Completed: false);
        }
        // Null: cut short, or the context has nothing to give.
        return new Outcome(Completed: !step.IsCancellationRequested);
    }

    private async Task<Outcome> IdleAsync(CancellationToken step)
    {
        Task work;
        lock (_gate)
        {
            if (_work.Task.IsCompleted) _work = NewSignal();
            work = _work.Task;
        }
        // Work added just before the reset already signalled; don't wait for another.
        if (_model.HasWork) return new Outcome(Completed: true);
        try
        {
            await work.WaitAsync(step).ConfigureAwait(false);
            return new Outcome(Completed: true);
        }
        catch (OperationCanceledException)
        {
            return new Outcome(Completed: false);
        }
    }
}
