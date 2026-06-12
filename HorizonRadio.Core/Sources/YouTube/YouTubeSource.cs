using System.Diagnostics;
using HorizonRadio.Core.Audio;
using HorizonRadio.Core.Models;

namespace HorizonRadio.Core.Sources.YouTube;

/// <summary>
/// YouTube audio source. Resolves a single-video or playlist URL via yt-dlp into
/// a flat entry list, then plays each entry through a <see cref="YouTubePlayableItem"/>
/// — which does the per-track resolve + ffmpeg decode, shared with the mix engine.
/// This source owns iteration order (shuffle) and transport
/// (next/prev/restart/pause); the decode lives in exactly one place.
/// </summary>
public sealed class YouTubeSource(YouTubeOptions options) : IAudioSource, ITransportControls, IPlaybackProgress
{
    public string Id => "youtube";
    public string DisplayName => "YouTube";

    public event Action<Track>? TrackChanged;
    public event Action<bool>? PausedChanged;

    // The entry currently playing — owns this track's progress/duration.
    private volatile PlayableItem? _activeItem;

    private CancellationTokenSource? _stopCts;
    private Task? _runLoop;
    private List<YtDlpClient.Entry> _entries = new();
    private readonly PlayOrder _order = new();

    private CancellationTokenSource? _trackCts;
    private volatile bool _stepBackwards;
    private volatile bool _restartCurrent;
    private volatile int _shuffleReq = -1;

    private volatile bool _paused;
    private readonly ManualResetEventSlim _resumeGate = new(initialState: true);

    private static void Log(string msg) => Debug.WriteLine($"[hzn-yt] {msg}");

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
    public bool CanSkipNext => _entries.Count > 1;
    public bool CanSkipPrevious => _entries.Count > 1;
    public bool IsPaused => _paused;
    public bool CanShuffle => _entries.Count > 1;
    public bool IsShuffled => _order.Shuffled;

    public TimeSpan? Duration => _activeItem?.Duration;
    public TimeSpan Position => _activeItem?.Position ?? TimeSpan.Zero;
    public bool CanSeek => _activeItem?.CanSeek ?? false;

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
        // Placeholder until the first entry resolves, mirroring the old UX.
        TrackChanged?.Invoke(new Track(
            Title: "Resolving…", Artist: options.Url, Album: null, AlbumArt: null,
            SourceId: Id, SourceDisplay: DisplayName, ExternalId: null));

        try
        {
            _entries = [.. await YtDlpClient.EnumerateAsync(options.YtDlpPath, options.Url, ct).ConfigureAwait(false)];
        }
        catch (OperationCanceledException)
        {
            return;
        }
        catch (Exception ex)
        {
            Log($"enumerate failed: {ex.Message}");
            TrackChanged?.Invoke(new Track(
                Title: "yt-dlp failed", Artist: ex.Message, Album: null, AlbumArt: null,
                SourceId: Id, SourceDisplay: DisplayName, ExternalId: null));
            return;
        }

        if (_entries.Count == 0)
        {
            Log("no entries resolved; idle");
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

        _order.Reset(_entries.Count);
        ApplyShuffleRequest(keepCurrent: false);

        while (!ct.IsCancellationRequested && _order.CurrentIndex >= 0)
        {
            using var trackCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            _trackCts = trackCts;
            _stepBackwards = false;

            var entry = _entries[_order.CurrentIndex];
            var item = new YouTubePlayableItem(entry, options.YtDlpPath, options.FfmpegPath, options.EnableVolumeNormalisation);
            _activeItem = item;
            Log($"track {_order.CurrentIndex + 1}/{_entries.Count}: {entry.Title}");

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
                // Per-track skip; the order advances below.
            }
            catch (Exception ex)
            {
                Log($"track {entry.Id} failed: {ex.GetType().Name}: {ex.Message}");
            }

            if (ReferenceEquals(_trackCts, trackCts)) _trackCts = null;

            ApplyShuffleRequest(keepCurrent: true);

            if (_restartCurrent) _restartCurrent = false;          // replay same entry
            else if (_stepBackwards) _order.Retreat(wrap: false);  // clamp at start
            else _order.Advance(wrap: false);                      // off end -> loop ends
        }
    }

    private void ApplyShuffleRequest(bool keepCurrent)
    {
        int req = _shuffleReq;
        if (req < 0) return;
        _shuffleReq = -1;
        _order.SetShuffle(req == 1, keepCurrent);
    }
}

public sealed class YouTubeOptions
{
    public required string YtDlpPath { get; init; }
    public required string FfmpegPath { get; init; }
    public required string Url { get; init; }
    public bool EnableVolumeNormalisation { get; init; }
}
