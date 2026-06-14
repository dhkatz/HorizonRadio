using System;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using HorizonRadio.Core.Audio;
using HorizonRadio.Core.Models;

namespace HorizonRadio.Core.Sources.Radio;

/// <summary>
/// Single-start internet-radio source: plays one station (the URL configured on the
/// Sources tab, or a single search/quick-play locator) by driving one
/// <see cref="RadioPlayableItem"/> and forwarding its live metadata to
/// <see cref="TrackChanged"/>. The queue path uses the item directly via
/// <see cref="RadioContentPlayer"/>; this wrapper exists for the "make Internet Radio
/// the active source" path, mirroring <see cref="YouTube.YouTubeSource"/>.
/// </summary>
public sealed class RadioSource(string locator, string ffmpegPath, RadioBrowserClient directory)
    : IAudioSource, ITransportControls, IPlaybackProgress
{
    public string Id => RadioSourceFactory.SourceId;
    public string DisplayName => "Internet Radio";

    public event Action<Track>? TrackChanged;
    public event Action<bool>? PausedChanged;

    private volatile RadioPlayableItem? _item;
    private CancellationTokenSource? _stopCts;
    private Task? _runLoop;

    private volatile bool _paused;
    private readonly ManualResetEventSlim _resumeGate = new(initialState: true);

    private static void Log(string msg) => Debug.WriteLine($"[hzn-radio] {msg}");

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
        _resumeGate.Set();
        if (_runLoop != null)
        {
            try { await _runLoop.ConfigureAwait(false); }
            catch { }
            _runLoop = null;
        }
        _stopCts?.Dispose();
        _stopCts = null;
        _item = null;
    }

    public async ValueTask DisposeAsync()
    {
        await StopAsync().ConfigureAwait(false);
        _resumeGate.Dispose();
    }

    // -- ITransportControls (pause only; a live station has no next/prev/seek) --
    public bool CanPause => true;
    public bool CanSkipNext => false;
    public bool CanSkipPrevious => false;
    public bool IsPaused => _paused;
    public bool CanShuffle => false;
    public bool IsShuffled => false;

    public Task SetShuffleAsync(bool enabled) => Task.CompletedTask;

    public Task TogglePauseAsync()
    {
        _paused = !_paused;
        if (_paused) _resumeGate.Reset();
        else _resumeGate.Set();
        PausedChanged?.Invoke(_paused);
        return Task.CompletedTask;
    }

    public Task NextAsync() => Task.CompletedTask;
    public Task PreviousAsync() => Task.CompletedTask;
    public Task RestartAsync() => Task.CompletedTask;

    // -- IPlaybackProgress --
    public TimeSpan? Duration => null;
    public TimeSpan Position => _item?.Position ?? TimeSpan.Zero;
    public bool CanSeek => false;
    public Task SeekAsync(TimeSpan position) => Task.CompletedTask;

    private async Task RunAsync(IPcmSink sink, CancellationToken ct)
    {
        // Resolve the locator -> station here (off the caller's thread); a radio:// locator
        // hits the directory over the network.
        RadioStation station;
        try
        {
            station = await RadioContentPlayer.ResolveStationAsync(locator, directory, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) { return; }
        catch (Exception ex)
        {
            Log($"resolve failed: {ex.Message}");
            TrackChanged?.Invoke(new Track("Internet Radio", ex.Message, null, null, Id, DisplayName));
            return;
        }

        var item = new RadioPlayableItem(station, ffmpegPath);
        _item = item;

        // Show the station immediately, before the connect/decode warms up.
        TrackChanged?.Invoke(item.Metadata);

        var ctx = new PumpContext
        {
            Sink = sink,
            IsPaused = () => _paused,
            ResumeGate = _resumeGate,
            OnStarted = it => TrackChanged?.Invoke(it.Metadata),
            OnMetadataUpdated = it => TrackChanged?.Invoke(it.Metadata),
        };

        try
        {
            await item.PlayAsync(ctx, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { Log($"play failed: {ex.Message}"); }
    }
}
