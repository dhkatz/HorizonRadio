using System;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using HorizonRadio.Core.Models;
using HorizonRadio.Core.Sources;

namespace HorizonRadio.Core.Metadata;

public sealed class EnrichmentService : IAsyncDisposable
{
    private readonly SourceRunner _runner;
    private IMetadataProvider? _provider;
    private CancellationTokenSource? _inflight;

    public event Action<Track>? TrackEnriched;

    public EnrichmentService(SourceRunner runner, IMetadataProvider? provider = null)
    {
        _runner = runner;
        _provider = provider;
        _runner.TrackChanged += OnSourceTrackChanged;
    }

    public void SetProvider(IMetadataProvider? provider)
    {
        var previous = _provider;
        _provider = provider;
        _inflight?.Cancel();
        _inflight?.Dispose();
        _inflight = null;
        if (previous != null && !ReferenceEquals(previous, provider))
            _ = DisposeProviderAsync(previous);
    }

    public IMetadataProvider? CurrentProvider => _provider;

    private void OnSourceTrackChanged(Track t)
    {
        var provider = _provider;
        if (provider == null) return;

        var prev = _inflight;
        var current = new CancellationTokenSource();
        _inflight = current;
        prev?.Cancel();
        prev?.Dispose();

        var ct = current.Token;
        _ = Task.Run(async () =>
        {
            try
            {
                var enriched = await provider.EnrichAsync(t, ct).ConfigureAwait(false);
                if (enriched == null || ct.IsCancellationRequested) return;

                if (enriched.AlbumArt == t.AlbumArt &&
                    enriched.Album == t.Album &&
                    enriched.Artist == t.Artist &&
                    enriched.Title == t.Title) return;

                TrackEnriched?.Invoke(enriched);
            }
            catch (OperationCanceledException) { }
            catch (Exception ex) { Debug.WriteLine($"[hzn-enrich] {ex.GetType().Name}: {ex.Message}"); }
        }, ct);
    }

    public async ValueTask DisposeAsync()
    {
        _runner.TrackChanged -= OnSourceTrackChanged;
        _inflight?.Cancel();
        _inflight?.Dispose();
        if (_provider != null) await _provider.DisposeAsync().ConfigureAwait(false);
    }

    private static async Task DisposeProviderAsync(IMetadataProvider provider)
    {
        try { await provider.DisposeAsync().ConfigureAwait(false); }
        catch (Exception ex) { Debug.WriteLine($"[hzn-enrich] provider dispose: {ex.Message}"); }
    }
}
