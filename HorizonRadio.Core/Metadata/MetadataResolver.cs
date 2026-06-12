using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using HorizonRadio.Core.Models;

namespace HorizonRadio.Core.Metadata;

/// <summary>
/// The metadata pipeline: given a seed <see cref="Track"/> (the source's own best
/// effort), it asks each enabled contributor — in the user's priority order — what
/// it knows, then merges the contributions per the <see cref="MetadataPolicy"/>
/// (per-field, honoring forced overrides). The source is contributor #0.
///
/// Each contributor sees a working query improved by earlier ones (a blank artist
/// filled in upstream means a downstream text search can actually find the track).
/// Contributors own their own caching, so resolving the same track again is cheap —
/// which is what makes the list (queue / mixes) enrichment affordable.
///
/// This is also directly callable by view models for list enrichment, not just by
/// <see cref="EnrichmentService"/> at play time.
/// </summary>
public sealed class MetadataResolver : IAsyncDisposable
{
    private readonly object _lock = new();
    private IReadOnlyList<IMetadataProvider> _contributors = [];
    private MetadataPolicy _policy = MetadataPolicy.Empty;

    private static void Log(string msg) => Debug.WriteLine($"[hzn-resolve] {msg}");

    /// <summary>Swap the live contributor set + policy (from the Metadata tab).
    /// Disposes contributors that are no longer present.</summary>
    public void Configure(IReadOnlyList<IMetadataProvider> contributors, MetadataPolicy policy)
    {
        IReadOnlyList<IMetadataProvider> old;
        lock (_lock)
        {
            old = _contributors;
            _contributors = contributors;
            _policy = policy;
        }
        foreach (var c in old)
            if (!contributors.Contains(c)) _ = DisposeQuietlyAsync(c);
    }

    /// <summary>True when at least one network contributor is configured (the source
    /// alone needs no resolve pass).</summary>
    public bool HasContributors { get { lock (_lock) return _contributors.Count > 0; } }

    public async Task<Track> ResolveAsync(Track seed, CancellationToken ct)
    {
        IReadOnlyList<IMetadataProvider> contributors;
        MetadataPolicy policy;
        lock (_lock) { contributors = _contributors; policy = _policy; }
        if (contributors.Count == 0) return seed;

        var byId = new Dictionary<string, MetadataContribution>(StringComparer.Ordinal)
        {
            [MetadataPolicy.SourceId] = SourceContribution(seed),
        };

        // Working query: the source's fields, with blanks filled by earlier
        // contributors so later lookups search against something usable.
        string title = seed.Title;
        string artist = seed.Artist;
        string? album = seed.Album;

        foreach (var id in policy.Order)
        {
            if (id == MetadataPolicy.SourceId) continue;
            var contributor = contributors.FirstOrDefault(x => x.Id == id);
            if (contributor == null) continue;

            MetadataContribution? contribution;
            try
            {
                contribution = await contributor
                    .ContributeAsync(new MetadataQuery(seed.SourceId, title, artist, album, seed.ExternalId), ct)
                    .ConfigureAwait(false);
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex) { Log($"{id}: {ex.GetType().Name}: {ex.Message}"); continue; }

            if (contribution is null || contribution.IsEmpty) continue;
            byId[id] = contribution;

            if (string.IsNullOrEmpty(artist) && !string.IsNullOrEmpty(contribution.Artist)) artist = contribution.Artist!;
            if (string.IsNullOrEmpty(album) && !string.IsNullOrEmpty(contribution.Album)) album = contribution.Album;
        }

        return Merge(seed, byId, policy);
    }

    private static MetadataContribution SourceContribution(Track t) => new(
        Title: string.IsNullOrEmpty(t.Title) ? null : t.Title,
        Artist: string.IsNullOrEmpty(t.Artist) ? null : t.Artist,
        Album: t.Album,
        Art: t.AlbumArt,
        Year: t.Year);

    private static Track Merge(Track seed, IReadOnlyDictionary<string, MetadataContribution> byId, MetadataPolicy policy) => seed with
    {
        Title = Pick(MetadataField.Title, byId, policy)?.Title ?? seed.Title,
        Artist = Pick(MetadataField.Artist, byId, policy)?.Artist ?? seed.Artist,
        Album = Pick(MetadataField.Album, byId, policy)?.Album ?? seed.Album,
        AlbumArt = Pick(MetadataField.Art, byId, policy)?.Art ?? seed.AlbumArt,
        Year = Pick(MetadataField.Year, byId, policy)?.Year ?? seed.Year,
    };

    // Resolve one field: a forced contributor wins if it supplied the field,
    // otherwise the first contributor in priority order that supplied it.
    private static MetadataContribution? Pick(
        MetadataField field,
        IReadOnlyDictionary<string, MetadataContribution> byId,
        MetadataPolicy policy)
    {
        var forced = policy.ForcedFor(field);
        if (forced != null && byId.TryGetValue(forced, out var f) && f.Has(field)) return f;

        foreach (var id in policy.Order)
            if (byId.TryGetValue(id, out var c) && c.Has(field)) return c;

        return null;
    }

    private static async Task DisposeQuietlyAsync(IMetadataProvider provider)
    {
        try { await provider.DisposeAsync().ConfigureAwait(false); }
        catch (Exception ex) { Log($"dispose {provider.Id}: {ex.Message}"); }
    }

    public async ValueTask DisposeAsync()
    {
        IReadOnlyList<IMetadataProvider> contributors;
        lock (_lock) { contributors = _contributors; _contributors = []; }
        foreach (var c in contributors) await DisposeQuietlyAsync(c).ConfigureAwait(false);
    }
}
