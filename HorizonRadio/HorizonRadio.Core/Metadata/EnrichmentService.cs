using System;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using HorizonRadio.Core.Models;
using HorizonRadio.Core.Sources;

namespace HorizonRadio.Core.Metadata;

/// <summary>
/// Play-time hook: when the active source publishes a track, run it through the
/// shared <see cref="MetadataResolver"/> and, if the resolved metadata differs,
/// raise <see cref="TrackEnriched"/> (the app pushes it to Now Playing + the HUD).
/// The resolver is owned by the app and shared with list enrichment; this service
/// just drives it on the playback event with single-flight cancellation.
/// </summary>
public sealed class EnrichmentService : IAsyncDisposable
{
    private readonly SourceRunner _runner;
    private readonly MetadataResolver _resolver;
    private CancellationTokenSource? _inflight;

    public event Action<Track>? TrackEnriched;

    public EnrichmentService(SourceRunner runner, MetadataResolver resolver)
    {
        _runner = runner;
        _resolver = resolver;
        _runner.TrackChanged += OnSourceTrackChanged;
    }

    private void OnSourceTrackChanged(Track t)
    {
        if (!_resolver.HasContributors) return;

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
                var enriched = await _resolver.ResolveAsync(t, ct).ConfigureAwait(false);
                if (ct.IsCancellationRequested || !Changed(t, enriched)) return;
                TrackEnriched?.Invoke(enriched);
            }
            catch (OperationCanceledException) { }
            catch (Exception ex) { Debug.WriteLine($"[hzn-enrich] {ex.GetType().Name}: {ex.Message}"); }
        }, ct);
    }

    private static bool Changed(Track a, Track b) =>
        a.Title != b.Title || a.Artist != b.Artist || a.Album != b.Album ||
        a.Year != b.Year || !ReferenceEquals(a.AlbumArt, b.AlbumArt);

    public ValueTask DisposeAsync()
    {
        _runner.TrackChanged -= OnSourceTrackChanged;
        _inflight?.Cancel();
        _inflight?.Dispose();
        return ValueTask.CompletedTask;
    }
}
