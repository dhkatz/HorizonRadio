using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using HorizonRadio.Core.Diagnostics;
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

    // Every contributor we've ever been handed, disposed only on our own dispose.
    // We deliberately do NOT dispose on reconfigure: the resolver is shared and a
    // background ResolveAsync awaits a contributor outside the lock, so disposing a
    // swapped-out provider mid-resolve would tear its HttpClient/semaphore out from
    // under it. The count is bounded by how often the user clicks Apply.
    private readonly List<IMetadataProvider> _owned = new();

    private static void Log(string msg) => Debug.WriteLine($"[hzn-resolve] {msg}");

    /// <summary>Swap the live contributor set + policy (from the Metadata tab).</summary>
    public void Configure(IReadOnlyList<IMetadataProvider> contributors, MetadataPolicy policy)
    {
        lock (_lock)
        {
            _contributors = contributors;
            _policy = policy;
            foreach (var c in contributors)
                if (!_owned.Contains(c)) _owned.Add(c);
        }
    }

    /// <summary>True when at least one network contributor is configured (the source
    /// alone needs no resolve pass).</summary>
    public bool HasContributors { get { lock (_lock) return _contributors.Count > 0; } }

    public async Task<Track> ResolveAsync(Track seed, CancellationToken ct)
    {
        // Non-song placeholders (e.g. a radio station card before the first track) must not be
        // searched — the station name false-matches unrelated catalog entries. Keep the source's
        // own art (the station logo) and skip the provider pass.
        if (!seed.Resolvable) return WithArtFallback(seed);

        IReadOnlyList<IMetadataProvider> contributors;
        MetadataPolicy policy;
        lock (_lock) { contributors = _contributors; policy = _policy; }
        if (contributors.Count == 0) return WithArtFallback(seed);

        // Ambiguous source titles attach alternative (artist, title) interpretations; try the
        // primary first, then candidates, keeping the one a catalog confirms.
        MetadataTrace.BeginResolve(seed);
        var result = seed.Candidates is { Count: > 0 }
            ? await ResolveBestAsync(seed, contributors, policy, ct).ConfigureAwait(false)
            : (await ResolveOneAsync(seed, contributors, policy, ct).ConfigureAwait(false)).Track;
        MetadataTrace.EndResolve(result);
        return result;
    }

    // Resolve the primary interpretation and the candidates, returning the first that a catalog
    // confirms. Interpretations that carry an artist are tried first: a title-only match (empty
    // artist) is unreliable — it can latch onto a cover/remix of the same title — so it must not
    // win ahead of an artist-corroborated one. A clean "Artist - Title" still short-circuits on
    // its first (primary) resolve. If nothing matches, the primary's resolution stands (display =
    // primary parse + station-logo fallback).
    private static async Task<Track> ResolveBestAsync(
        Track seed, IReadOnlyList<IMetadataProvider> contributors, MetadataPolicy policy, CancellationToken ct)
    {
        var primary = seed with { Candidates = null };
        var all = new List<Track> { primary };
        foreach (var c in seed.Candidates!)
            if (!string.IsNullOrWhiteSpace(c.Title))
                all.Add(primary with { Title = c.Title, Artist = c.Artist ?? "", ExternalId = null });

        var ordered = all.Where(t => !string.IsNullOrWhiteSpace(t.Artist))
                         .Concat(all.Where(t => string.IsNullOrWhiteSpace(t.Artist)));

        Track? primaryTrack = null;
        foreach (var s in ordered)
        {
            var (track, matched) = await ResolveOneAsync(s, contributors, policy, ct).ConfigureAwait(false);
            if (ReferenceEquals(s, primary)) primaryTrack = track;
            if (matched) return track;
        }

        return primaryTrack ?? (await ResolveOneAsync(primary, contributors, policy, ct).ConfigureAwait(false)).Track;
    }

    // Run one seed through the contributor chain. Matched = a network contributor produced a
    // non-empty contribution (they only do so when their own match guard passed) — i.e. a
    // catalog confirmed this interpretation.
    private static async Task<(Track Track, bool Matched)> ResolveOneAsync(
        Track seed, IReadOnlyList<IMetadataProvider> contributors, MetadataPolicy policy, CancellationToken ct)
    {
        var byId = new Dictionary<string, MetadataContribution>(StringComparer.Ordinal)
        {
            [MetadataPolicy.SourceId] = SourceContribution(seed),
        };

        // Working query: the source's fields, with blanks filled by earlier
        // contributors so later lookups search against something usable.
        string title = seed.Title;
        string artist = seed.Artist;
        string? album = seed.Album;

        MetadataTrace.BeginAttempt(artist, title);

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
            catch (Exception ex)
            {
                Log($"{id}: {ex.GetType().Name}: {ex.Message}");
                MetadataTrace.Provider(id, matched: false, null, null, null, 0);
                continue;
            }

            if (contribution is null || contribution.IsEmpty)
            {
                MetadataTrace.Provider(id, matched: false,
                    contribution?.Artist, contribution?.Title, contribution?.Album, contribution?.Art?.Length ?? 0);
                continue;
            }
            byId[id] = contribution;
            MetadataTrace.Provider(id, matched: true,
                contribution.Artist, contribution.Title, contribution.Album, contribution.Art?.Length ?? 0);

            if (string.IsNullOrEmpty(artist) && !string.IsNullOrEmpty(contribution.Artist)) artist = contribution.Artist!;
            if (string.IsNullOrEmpty(album) && !string.IsNullOrEmpty(contribution.Album)) album = contribution.Album;
        }

        // More than just the source contributed → a catalog matched this interpretation.
        return (Merge(seed, byId, policy) with { Candidates = null }, byId.Count > 1);
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
        // Real (source/provider) art wins; the source's fallback art (a radio station
        // logo) only fills in when nothing better was found.
        AlbumArt = Pick(MetadataField.Art, byId, policy)?.Art ?? seed.AlbumArt ?? seed.FallbackArt,
        Year = Pick(MetadataField.Year, byId, policy)?.Year ?? seed.Year,
    };

    // Applied when there are no providers to merge: still honor the source's fallback art.
    private static Track WithArtFallback(Track t) =>
        t.AlbumArt is { Length: > 0 } || t.FallbackArt is not { Length: > 0 }
            ? t
            : t with { AlbumArt = t.FallbackArt };

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
        List<IMetadataProvider> owned;
        lock (_lock) { owned = new List<IMetadataProvider>(_owned); _owned.Clear(); _contributors = []; }
        foreach (var c in owned) await DisposeQuietlyAsync(c).ConfigureAwait(false);
    }
}
