using System;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using HorizonRadio.Core.Models;
using HorizonRadio.Core.Sources;

namespace HorizonRadio.Core.Metadata;

/// <summary>
/// Subscribes to a <see cref="SourceRunner"/>'s
/// <see cref="SourceRunner.TrackChanged"/> event, runs each track
/// through the currently-selected <see cref="IMetadataEnricher"/> in
/// the background, and re-publishes the enriched track via
/// <see cref="TrackEnriched"/>.
///
/// The enricher is swappable at runtime (Metadata tab) so the user
/// can switch between Spotify / MusicBrainz / off without restarting.
/// Setting it to null disables enrichment cleanly.
///
/// Each TrackChanged cancels any in-flight enrichment so a fast-
/// skipping playlist doesn't queue a backlog of stale requests.
/// </summary>
public sealed class EnrichmentService : IAsyncDisposable
{
    private readonly SourceRunner       _runner;
    private IMetadataEnricher?          _enricher;
    private CancellationTokenSource?    _inflight;

    public event Action<Track>? TrackEnriched;

    public EnrichmentService(SourceRunner runner, IMetadataEnricher? enricher = null)
    {
        _runner   = runner;
        _enricher = enricher;
        _runner.TrackChanged += OnSourceTrackChanged;
    }

    /// <summary>Replace the active enricher. Pass null to disable
    /// enrichment without tearing down the service.</summary>
    public void SetEnricher(IMetadataEnricher? enricher)
    {
        _enricher = enricher;
        _inflight?.Cancel();    // any in-flight call against the old one is stale now
    }

    public IMetadataEnricher? CurrentEnricher => _enricher;

    private static void Log(string msg) => Debug.WriteLine($"[hzn-enrich] {msg}");

    private void OnSourceTrackChanged(Track t)
    {
        var prev = _inflight;
        _inflight = new CancellationTokenSource();
        prev?.Cancel();

        var enricher = _enricher;
        if (enricher == null) return;   // enrichment disabled

        var ct = _inflight.Token;
        _ = Task.Run(async () =>
        {
            try
            {
                var enriched = await enricher.EnrichAsync(t, ct).ConfigureAwait(false);
                if (enriched == null || ct.IsCancellationRequested) return;

                if (enriched.AlbumArt == t.AlbumArt &&
                    enriched.Album    == t.Album    &&
                    enriched.Artist   == t.Artist   &&
                    enriched.Title    == t.Title) return;

                TrackEnriched?.Invoke(enriched);
            }
            catch (OperationCanceledException) { /* normal on skip */ }
            catch (Exception ex) { Log($"enrich failed: {ex.GetType().Name}: {ex.Message}"); }
        }, ct);
    }

    public ValueTask DisposeAsync()
    {
        _runner.TrackChanged -= OnSourceTrackChanged;
        _inflight?.Cancel();
        _inflight?.Dispose();
        return ValueTask.CompletedTask;
    }
}
