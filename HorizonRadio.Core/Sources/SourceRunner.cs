using HorizonRadio.Core.Models;
using HorizonRadio.Core.Sources.Config;

namespace HorizonRadio.Core.Sources;

public sealed class SourceRunner(IPcmSink sink) : IAsyncDisposable
{
    private IAudioSource? _active;
    private CancellationTokenSource? _cts;

    public IAudioSourceFactory? ActiveFactory { get; private set; }
    public IAudioSource? ActiveSource => _active;
    public bool IsRunning => _active != null;

    /// <summary>Global shuffle preference applied to each source as it starts
    /// (sources that support it; see <see cref="ITransportControls.CanShuffle"/>).
    /// The UI keeps this in sync with the persisted preference and toggles the
    /// already-running source directly via its transport controls.</summary>
    public bool Shuffle { get; set; }

    public event Action<Track>? TrackChanged;
    public event Action<IAudioSourceFactory?>? ActiveSourceChanged;

    public async Task StartAsync(IAudioSourceFactory factory, ConfigValues values)
    {
        // Pre-flight before we stop what's already playing — a switch to a
        // source whose tools are missing shouldn't tear down the current
        // source first. Throws MissingToolException with a Tools-tab hint.
        SourceRequirements.EnsureToolsAvailable(factory, values);

        var source = factory.Create(values);
        await StartSourceAsync(source, factory).ConfigureAwait(false);
    }

    /// <summary>
    /// Start an already-built source. The factory path (<see cref="StartAsync"/>)
    /// funnels here after Create; a mix funnels here directly (it has no single
    /// factory — pass null, and the mix's own resolver/pre-flight handles its
    /// entries). <see cref="ActiveFactory"/> is null for a factory-less source.
    /// </summary>
    public async Task StartSourceAsync(IAudioSource source, IAudioSourceFactory? factory = null)
    {
        await StopAsync().ConfigureAwait(false);

        source.TrackChanged += OnTrackChanged;

        _cts = new CancellationTokenSource();
        _active = source;
        ActiveFactory = factory;
        ActiveSourceChanged?.Invoke(factory);

        // Apply the persisted shuffle preference before the source's run loop
        // starts, so a shuffle-on session randomizes from the first track.
        if (Shuffle && source is ITransportControls tc && tc.CanShuffle)
            await tc.SetShuffleAsync(true).ConfigureAwait(false);

        try
        {
            await source.StartAsync(sink, _cts.Token).ConfigureAwait(false);
        }
        catch
        {
            await StopAsync().ConfigureAwait(false);
            throw;
        }
    }

    public async Task StopAsync()
    {
        var src = _active;
        if (src == null) return;

        try { _cts?.Cancel(); } catch { }
        try { await src.StopAsync().ConfigureAwait(false); } catch (Exception) { }
        try { await src.DisposeAsync().ConfigureAwait(false); } catch { }

        src.TrackChanged -= OnTrackChanged;
        _active = null;
        ActiveFactory = null;
        _cts?.Dispose();
        _cts = null;
        ActiveSourceChanged?.Invoke(null);
    }

    private void OnTrackChanged(Track t) => TrackChanged?.Invoke(t);

    public async ValueTask DisposeAsync() => await StopAsync().ConfigureAwait(false);
}
