using System;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using HorizonRadio.Core.Models;
using HorizonRadio.Core.Sources.Config;

namespace HorizonRadio.Core.Sources;

/// <summary>
/// Holds the currently active <see cref="IAudioSource"/> and manages
/// its lifecycle. Owns the PCM sink, so the UI never sees it directly;
/// it just says "start this factory with these values" / "stop".
///
/// Single-source-at-a-time by design: switching sources stops the
/// current one before starting the new one, so the FMOD bridge always
/// sees one coherent PCM stream.
/// </summary>
public sealed class SourceRunner : IAsyncDisposable
{
    private readonly IPcmSink _sink;

    private IAudioSource?            _active;
    private CancellationTokenSource? _cts;

    public IAudioSourceFactory? ActiveFactory { get; private set; }
    public IAudioSource?        ActiveSource  => _active;
    public bool                 IsRunning     => _active != null;

    public event Action<Track>?              TrackChanged;
    public event Action<IAudioSourceFactory?>? ActiveSourceChanged;

    public SourceRunner(IPcmSink sink) { _sink = sink; }

    private static void Log(string msg) => Debug.WriteLine($"[hzn-runner] {msg}");

    /// <summary>Stop whatever is running and start the configured source
    /// from <paramref name="factory"/>. Throws whatever the factory throws
    /// on bad config (caller surfaces to UI).</summary>
    public async Task StartAsync(IAudioSourceFactory factory, ConfigValues values)
    {
        await StopAsync().ConfigureAwait(false);

        var source = factory.Create(values);
        source.TrackChanged += OnTrackChanged;

        _cts = new CancellationTokenSource();
        _active = source;
        ActiveFactory = factory;
        ActiveSourceChanged?.Invoke(factory);

        try
        {
            await source.StartAsync(_sink, _cts.Token).ConfigureAwait(false);
            Log($"started {factory.Id}");
        }
        catch (Exception ex)
        {
            Log($"start {factory.Id} failed: {ex.Message}");
            await StopAsync().ConfigureAwait(false);
            throw;
        }
    }

    public async Task StopAsync()
    {
        var src = _active;
        if (src == null) return;

        try { _cts?.Cancel(); } catch { }
        try { await src.StopAsync().ConfigureAwait(false); } catch (Exception ex) { Log($"stop: {ex.Message}"); }
        try { await src.DisposeAsync().ConfigureAwait(false); } catch { }

        src.TrackChanged -= OnTrackChanged;
        _active = null;
        ActiveFactory = null;
        _cts?.Dispose();
        _cts = null;
        ActiveSourceChanged?.Invoke(null);
        Log("stopped");
    }

    private void OnTrackChanged(Track t) => TrackChanged?.Invoke(t);

    public async ValueTask DisposeAsync() => await StopAsync().ConfigureAwait(false);
}
