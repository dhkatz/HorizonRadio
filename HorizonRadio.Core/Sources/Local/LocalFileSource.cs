using System.Diagnostics;
using HorizonRadio.Core.Audio;
using HorizonRadio.Core.Models;

namespace HorizonRadio.Core.Sources.Local;

/// <summary>
/// Plays through a <see cref="Playlist"/> of local audio files, looping at the
/// end. Owns the iteration order (incl. shuffle) and transport (pause/next/prev/
/// restart/seek); the per-file decode, tag read, paced pump, and seek live in
/// <see cref="LocalPlayableItem"/>, shared with the mix engine so the decode
/// path exists in exactly one place.
/// </summary>
public sealed class LocalFileSource(Playlist playlist) : IAudioSource, ITransportControls, IPlaybackProgress
{
    public string Id => "local";
    public string DisplayName => "Local Files";

    public event Action<Track>? TrackChanged;
    public event Action<bool>? PausedChanged;

    // The file currently playing — owns this track's progress/seek/duration.
    private volatile PlayableItem? _activeItem;

    private CancellationTokenSource? _stopCts;
    private Task? _runLoop;

    // Per-track CTS: cancelled to skip the current file (Next/Previous/Restart).
    private CancellationTokenSource? _trackCts;
    private volatile bool _stepBackwards;
    private volatile bool _restartCurrent;

    // Pending shuffle request: -1 none, 0 off, 1 on. Applied on the run-loop
    // thread (the only thread allowed to touch the playlist).
    private volatile int _shuffleReq = -1;

    private volatile bool _paused;
    private readonly ManualResetEventSlim _resumeGate = new(initialState: true);

    private static void Log(string msg) => Debug.WriteLine($"[hzn-local] {msg}");

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

    public bool CanPause => true;
    public bool CanSkipNext => playlist.Count > 1;
    public bool CanSkipPrevious => playlist.Count > 1;
    public bool IsPaused => _paused;
    public bool CanShuffle => playlist.Count > 1;
    public bool IsShuffled => playlist.Shuffle;

    public TimeSpan? Duration => _activeItem?.Duration;
    public TimeSpan Position => _activeItem?.Position ?? TimeSpan.Zero;
    public bool CanSeek => _activeItem?.CanSeek ?? false;

    public Task SeekAsync(TimeSpan position)
    {
        _activeItem?.Seek(position);
        return Task.CompletedTask;
    }

    public Task SetShuffleAsync(bool enabled)
    {
        _shuffleReq = enabled ? 1 : 0;
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

    private async Task RunAsync(IPcmSink sink, CancellationToken ct)
    {
        if (playlist.Count == 0)
        {
            Log("playlist is empty; source idle");
            return;
        }

        var pumpCtx = new PumpContext
        {
            Sink = sink,
            IsPaused = () => _paused,
            ResumeGate = _resumeGate,
            OnStarted = item =>
            {
                _activeItem = item;
                TrackChanged?.Invoke(item.Metadata);
            },
        };

        // Apply an initial shuffle request before the first track so a source
        // that starts shuffled gets a random first track (keepCurrent:false).
        ApplyShuffleRequest(keepCurrent: false);

        while (!ct.IsCancellationRequested)
        {
            var path = playlist.Current;
            if (path == null) break;

            using var trackCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            _trackCts = trackCts;
            _stepBackwards = false;

            var item = new LocalPlayableItem(path);
            _activeItem = item;

            Log($"opening {System.IO.Path.GetFileName(path)}");
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
            }
            catch (Exception ex)
            {
                Log($"decode failed for {path}: {ex.GetType().Name}: {ex.Message}");
            }

            if (ReferenceEquals(_trackCts, trackCts)) _trackCts = null;

            // Apply a mid-playback shuffle toggle here, while playlist.Current is
            // still the track that just played, so "keep current, shuffle rest"
            // pins the right track before we advance.
            ApplyShuffleRequest(keepCurrent: true);

            if (_restartCurrent) _restartCurrent = false; // replay same entry
            else if (_stepBackwards) playlist.Previous();
            else playlist.Next();
        }
    }

    private void ApplyShuffleRequest(bool keepCurrent)
    {
        int req = _shuffleReq;
        if (req < 0) return;
        _shuffleReq = -1;
        playlist.SetShuffle(req == 1, keepCurrent);
    }
}
