using System.Diagnostics;
using HorizonRadio.Core.Models;

namespace HorizonRadio.Core.Sources.Spotify;

/// <summary>
/// Plays a Spotify locator (one track, or a whole playlist/album) straight through
/// as a single <see cref="IAudioSource"/> — the direct Sources-tab "Play" path, the
/// Spotify analogue of <see cref="Local.LocalFileSource"/>. Each track's PCM pump,
/// position, and seek come from the shared <see cref="SpotifyPlaybackService"/>;
/// this just sequences the enumerated items and maps the player-bar transport
/// (pause/next/previous/restart) onto per-track cancellation, the way the queue
/// engine does. (The global queue and Mixes use <c>EnumerateAsync</c> directly and
/// never go through here.)
/// </summary>
internal sealed class SpotifyContentSource(ContentRef content, SpotifyContentPlayer player)
    : IAudioSource, ITransportControls, IPlaybackProgress
{
    public string Id => SpotifyContentSourceFactory.SourceId;
    public string DisplayName => "Spotify";

    public event Action<Track>? TrackChanged;
    public event Action<bool>? PausedChanged;

    private CancellationTokenSource? _stopCts;
    private CancellationTokenSource? _trackCts;
    private Task? _runLoop;

    private IReadOnlyList<PlayableItem> _items = [];
    private volatile int _index;
    private volatile int _count;
    private volatile PlayableItem? _activeItem;
    private volatile bool _paused;
    private volatile int _step = 1; // -1 previous, 0 restart, +1 next/normal
    private readonly ManualResetEventSlim _resumeGate = new(initialState: true);

    private static void Log(string msg) => Debug.WriteLine($"[hzn-spotify-src] {msg}");

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
            try { await _runLoop.ConfigureAwait(false); } catch { }
            _runLoop = null;
        }
        _stopCts?.Dispose(); _stopCts = null;
        _trackCts?.Dispose(); _trackCts = null;
        _activeItem = null;
    }

    public async ValueTask DisposeAsync()
    {
        await StopAsync().ConfigureAwait(false);
        _resumeGate.Dispose();
    }

    // -- ITransportControls --

    public bool CanPause => true;
    public bool CanSkipNext => _index < _count - 1;
    public bool CanSkipPrevious => _index > 0;
    public bool IsPaused => _paused;

    public Task TogglePauseAsync()
    {
        _paused = !_paused;
        if (_paused) _resumeGate.Reset(); else _resumeGate.Set();
        PausedChanged?.Invoke(_paused);
        return Task.CompletedTask;
    }

    public Task NextAsync() { _step = 1; _trackCts?.Cancel(); return Task.CompletedTask; }
    public Task PreviousAsync() { _step = -1; _trackCts?.Cancel(); return Task.CompletedTask; }
    public Task RestartAsync() { _step = 0; _trackCts?.Cancel(); return Task.CompletedTask; }

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
        try { _items = await player.EnumerateAsync(content, ct).ConfigureAwait(false); }
        catch (OperationCanceledException) { return; }
        catch (Exception ex) { Log($"enumerate failed: {ex.Message}"); return; }

        _count = _items.Count;
        if (_count == 0) return;

        var ctx = new PumpContext
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

        _index = 0;
        while (!ct.IsCancellationRequested && _index >= 0 && _index < _count)
        {
            var item = _items[_index];
            _activeItem = item;

            using var trackCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            _trackCts = trackCts;
            _step = 1; // default: advance; Next/Previous/Restart override during play

            try
            {
                await item.PlayAsync(ctx, trackCts.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { return; }
            catch (OperationCanceledException) { /* per-track skip/prev/restart */ }
            catch (Exception ex) { Log($"item failed: {ex.Message}"); }

            if (ReferenceEquals(_trackCts, trackCts)) _trackCts = null;

            _index += _step; // _step ∈ {-1,0,+1}
            if (_index < 0) _index = 0;
        }
    }
}
