using HorizonRadio.Core.Models;

namespace HorizonRadio.Core.Sources;

/// <summary>
/// The audio engine: runs one source at a time into the sink. Only the playback router
/// (<c>PlaybackRouter</c>) starts, stops, or parks it; everything else asks the router, and
/// reads what's playing from here.
/// </summary>
public sealed class SourceRunner(IPcmSink sink) : IAsyncDisposable
{
    // One start, stop, park or shuffle change at a time. The router already applies them one by
    // one; this is defence in depth, so no stray caller can leave two sources pumping.
    private readonly SemaphoreSlim _lock = new(1, 1);

    private volatile IAudioSource? _active;
    private CancellationTokenSource? _cts;

    public IAudioSourceFactory? ActiveFactory { get; private set; }
    public IAudioSource? ActiveSource => _active;
    public bool IsRunning => _active != null;

    /// <summary>The shuffle preference, applied — on or off — to every source as it starts or
    /// resumes (sources that support it; see <see cref="ITransportControls.CanShuffle"/>), so a
    /// parked channel catches up with a toggle made while it was away. Set through
    /// <see cref="SetShuffleAsync"/>, which also applies it to the source playing now.</summary>
    public bool Shuffle { get; internal set; }

    public event Action<Track>? TrackChanged;

    /// <summary>Raised whenever the active source changes (null factory for a factory-less one,
    /// such as a queue engine, and when nothing is left running). Handlers run while the runner
    /// is switching, so they must not call back into it.</summary>
    public event Action<IAudioSourceFactory?>? ActiveSourceChanged;

    /// <summary>Change the shuffle preference and apply it to the source playing now.</summary>
    internal async Task SetShuffleAsync(bool enabled)
    {
        await _lock.WaitAsync().ConfigureAwait(false);
        try
        {
            Shuffle = enabled;
            await ApplyShuffleAsync(_active).ConfigureAwait(false);
        }
        finally { _lock.Release(); }
    }

    /// <summary>
    /// Make <paramref name="source"/> the one running source, stopping (and disposing) whatever
    /// was running. <paramref name="factory"/> is the self-driven source's factory, or null for a
    /// factory-less one (a queue engine); it becomes <see cref="ActiveFactory"/>.
    /// </summary>
    internal async Task StartSourceAsync(IAudioSource source, IAudioSourceFactory? factory = null)
    {
        await _lock.WaitAsync().ConfigureAwait(false);
        try
        {
            await StopActiveAsync(dispose: true).ConfigureAwait(false);

            source.TrackChanged += OnTrackChanged;

            _cts = new CancellationTokenSource();
            _active = source;
            ActiveFactory = factory;
            ActiveSourceChanged?.Invoke(factory);

            // Before the source's run loop starts, so a shuffle-on session randomizes from the first
            // track, and a channel coming back follows a toggle made while it was parked.
            await ApplyShuffleAsync(source).ConfigureAwait(false);

            try
            {
                await source.StartAsync(sink, _cts.Token).ConfigureAwait(false);
            }
            catch
            {
                await StopActiveAsync(dispose: true).ConfigureAwait(false);
                throw;
            }
        }
        finally { _lock.Release(); }
    }

    /// <summary>Stop the active source and dispose it.</summary>
    internal async Task StopAsync()
    {
        await _lock.WaitAsync().ConfigureAwait(false);
        try { await StopActiveAsync(dispose: true).ConfigureAwait(false); }
        finally { _lock.Release(); }
    }

    /// <summary>
    /// Stop the active source <em>without disposing it</em> and hand it back, so its owner can
    /// start it again later with its state intact (a station channel resuming where it left
    /// off). The caller now owns the returned source's lifetime. Null if nothing was running.
    /// </summary>
    internal async Task<IAudioSource?> ParkAsync()
    {
        await _lock.WaitAsync().ConfigureAwait(false);
        try { return await StopActiveAsync(dispose: false).ConfigureAwait(false); }
        finally { _lock.Release(); }
    }

    private async Task ApplyShuffleAsync(IAudioSource? source)
    {
        if (source is ITransportControls { CanShuffle: true } tc)
            await tc.SetShuffleAsync(Shuffle).ConfigureAwait(false);
    }

    // Under the lock.
    private async Task<IAudioSource?> StopActiveAsync(bool dispose)
    {
        var src = _active;
        if (src == null) return null;

        try { _cts?.Cancel(); } catch { }
        try { await src.StopAsync().ConfigureAwait(false); } catch (Exception) { }
        if (dispose)
        {
            try { await src.DisposeAsync().ConfigureAwait(false); } catch { }
        }

        src.TrackChanged -= OnTrackChanged;
        _active = null;
        ActiveFactory = null;
        _cts?.Dispose();
        _cts = null;
        ActiveSourceChanged?.Invoke(null);
        return dispose ? null : src;
    }

    private void OnTrackChanged(Track t) => TrackChanged?.Invoke(t);

    public async ValueTask DisposeAsync() => await StopAsync().ConfigureAwait(false);
}
