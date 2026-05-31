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

    public event Action<Track>? TrackChanged;
    public event Action<IAudioSourceFactory?>? ActiveSourceChanged;

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
