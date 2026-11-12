using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using HorizonRadio.Core.Diagnostics;
using HorizonRadio.Sdk;
using HorizonRadio.Sdk.Metadata;
using HorizonRadio.Sdk.Models;

namespace HorizonRadio.Core.Metadata;

/// <summary>
/// The metadata pipeline: given a seed <see cref="Track"/> (the source's own best
/// effort), it asks each enabled contributor what it knows, then merges the
/// contributions in the user's priority order per the <see cref="MetadataPolicy"/>
/// (per-field, honoring forced overrides). The source is contributor #0.
///
/// Contributors are asked at the same time when the seed has an artist. With a blank
/// artist they're asked in priority order instead, so an artist an earlier one fills in
/// lets a later text search find the track. The merge is by priority either way, so the
/// answer doesn't depend on who replied first.
///
/// Identical lookups share one run: when a song starts, Now Playing, the queue's tile,
/// and History all ask about it at once, and each provider should be asked once.
///
/// One slow provider shouldn't hold the song's title back either (MusicBrainz waits 2–6 s
/// per Cover Art Archive image). A caller that passes an early-result callback gets the
/// merge of the answers so far once a catalog has matched and the rest are still busy after
/// a short grace period, then the full result when they finish.
/// Contributors also own their own caching, so resolving the same track again later is
/// cheap, which is what makes the list (queue / mixes / History) enrichment affordable.
///
/// This is also directly callable by view models for list enrichment, not just by
/// <see cref="EnrichmentService"/> at play time. Lists pass
/// <see cref="ResolvePriority.Background"/> so the song playing now goes first.
/// </summary>
/// <param name="log">Where a provider that couldn't answer is reported (the Console).</param>
public sealed class MetadataResolver(IPluginLog log) : IAsyncDisposable
{
    private readonly IPluginLog _log = log;
    private readonly object _lock = new();
    private IReadOnlyList<IMetadataProvider> _contributors = [];
    private MetadataPolicy _policy = MetadataPolicy.Empty;
    private int _generation;

    // Every contributor we've ever been handed, disposed only on our own dispose.
    // We deliberately do NOT dispose on reconfigure: the resolver is shared and a
    // background ResolveAsync awaits a contributor outside the lock, so disposing a
    // swapped-out provider mid-resolve would tear its HttpClient/semaphore out from
    // under it. The count is bounded by how often the user clicks Apply.
    private readonly List<IMetadataProvider> _owned = new();

    // Lookups in flight, by what they search for. Guarded by _lock.
    private readonly Dictionary<string, Flight> _flights = new(StringComparer.Ordinal);

    // After the first catalog match, how long to wait for the other providers before showing
    // what we have. Long enough that a fast set of providers gives one update, not two.
    private static readonly TimeSpan EarlyGrace = TimeSpan.FromMilliseconds(400);

    private static void Log(string msg) => Trace.WriteLine($"[hzn-resolve] {msg}");

    /// <summary>Swap the live contributor set + policy (from the Metadata tab).</summary>
    public void Configure(IReadOnlyList<IMetadataProvider> contributors, MetadataPolicy policy)
    {
        lock (_lock)
        {
            _contributors = contributors;
            _policy = policy;
            _generation++; // a lookup started under the old set isn't shared with new callers
            foreach (var c in contributors)
                if (!_owned.Contains(c)) _owned.Add(c);
        }
    }

    /// <summary>True when at least one network contributor is configured (the source
    /// alone needs no resolve pass).</summary>
    public bool HasContributors { get { lock (_lock) return _contributors.Count > 0; } }

    /// <param name="onEarlyResult">Called at most once, before this returns, with the merge of
    /// the answers so far when a catalog has matched but slower providers are still working.
    /// The returned track is the full result, which may differ (e.g. art from a
    /// higher-priority provider that answered later).</param>
    public async Task<Track> ResolveAsync(
        Track seed, CancellationToken ct, ResolvePriority priority = ResolvePriority.Foreground,
        Action<Track>? onEarlyResult = null) =>
        (await ResolveDetailedAsync(seed, ct, priority,
            onEarlyResult is null ? null : early => onEarlyResult(early.Track)).ConfigureAwait(false)).Track;

    /// <summary>
    /// Like <see cref="ResolveAsync"/>, but also reports whether a catalog actually confirmed
    /// the track (<c>Matched</c>) — i.e. a network contributor produced a non-empty
    /// contribution, not just the source echoing its own seed. Play history uses this to flag
    /// freeform (radio) songs we couldn't identify, separately from the merged metadata it
    /// shows. A non-resolvable placeholder or a no-contributor setup reports <c>false</c>.
    /// </summary>
    public async Task<MetadataResult> ResolveDetailedAsync(
        Track seed, CancellationToken ct, ResolvePriority priority = ResolvePriority.Foreground,
        Action<MetadataResult>? onEarlyResult = null)
    {
        // Non-song placeholders (e.g. a radio station card before the first track) must not be
        // searched — the station name false-matches unrelated catalog entries. Keep the source's
        // own art (the station logo) and skip the provider pass.
        if (!seed.Resolvable) return new MetadataResult(WithArtFallback(seed), false, []);

        IReadOnlyList<IMetadataProvider> contributors;
        MetadataPolicy policy;
        int generation;
        lock (_lock) { contributors = _contributors; policy = _policy; generation = _generation; }
        if (contributors.Count == 0) return new MetadataResult(WithArtFallback(seed), false, []);

        Action<Lookup>? early = onEarlyResult is null ? null : l => onEarlyResult(Finish(seed, l, policy));
        var lookup = await JoinAsync(seed, contributors, policy, generation, priority, early, ct).ConfigureAwait(false);
        return Finish(seed, lookup, policy);
    }

    // -- Shared lookups --

    // What one run found: the interpretation a catalog confirmed (or the primary when none
    // did) and what each network contributor said about it. Holds no seed art, so callers
    // with different seeds for the same text can share it; each merges with its own seed.
    private sealed record Lookup(Interpretation Interp, IReadOnlyDictionary<string, MetadataContribution> Network);

    private readonly record struct Interpretation(string Title, string Artist, string? ExternalId);

    // Waiters, Finished, and every use of Cts are guarded by the resolver's _lock, so a waiter
    // giving up can never cancel a source the run already disposed.
    private sealed class Flight(PriorityTicket ticket) : IDisposable
    {
        public readonly PriorityTicket Ticket = ticket;
        public readonly CancellationTokenSource Cts = new();
        public Task<Lookup> Task = null!;
        public int Waiters;
        public bool Finished;

        // The answers so far, once a catalog has matched; replaced as each answer arrives.
        private volatile Lookup? _latest;
        public readonly TaskCompletionSource Matched = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Lookup? Latest => _latest;

        public void Report(Lookup soFar)
        {
            _latest = soFar;
            Matched.TrySetResult();
        }

        // Stop the run unless it already finished. Call under _lock.
        public void CancelIfRunning()
        {
            if (!Finished) Cts.Cancel();
        }

        public void Dispose() => Cts.Dispose();
    }

    // Join the run already searching for this seed's text, or start one. The run is
    // cancelled only when everyone waiting on it has given up (a song skipped before its
    // lookup finished), so one caller cancelling doesn't fail the others.
    private async Task<Lookup> JoinAsync(
        Track seed, IReadOnlyList<IMetadataProvider> contributors, MetadataPolicy policy,
        int generation, ResolvePriority priority, Action<Lookup>? early, CancellationToken ct)
    {
        var key = FlightKey(seed, generation);
        Flight flight;
        lock (_lock)
        {
            if (_flights.TryGetValue(key, out var existing))
            {
                flight = existing;
                if (priority == ResolvePriority.Foreground) flight.Ticket.Promote();
            }
            else
            {
                flight = new Flight(new PriorityTicket(priority));
                var started = flight;
                // Off this thread so the run never starts inside the lock.
                flight.Task = Task.Run(() => RunAsync(started, key, seed, contributors, policy));
                _flights[key] = flight;
            }
            flight.Waiters++;
        }

        try
        {
            if (early != null) await OfferEarlyAsync(flight, early, ct).ConfigureAwait(false);
            return await flight.Task.WaitAsync(ct).ConfigureAwait(false);
        }
        finally
        {
            lock (_lock)
            {
                if (--flight.Waiters == 0 && !flight.Finished)
                {
                    flight.CancelIfRunning();
                    // A later caller for the same song starts fresh instead of joining a dead run.
                    if (_flights.TryGetValue(key, out var current) && current == flight) _flights.Remove(key);
                }
            }
        }
    }

    // Once a catalog has matched, give the other providers a short grace period; if they're
    // still working after it, hand over the answers so far.
    private static async Task OfferEarlyAsync(Flight flight, Action<Lookup> early, CancellationToken ct)
    {
        if (await Task.WhenAny(flight.Matched.Task, flight.Task).WaitAsync(ct).ConfigureAwait(false) == flight.Task)
            return;
        var grace = Task.Delay(EarlyGrace, ct);
        if (await Task.WhenAny(flight.Task, grace).ConfigureAwait(false) == flight.Task) return;
        ct.ThrowIfCancellationRequested();
        if (flight.Latest is { } soFar) early(soFar);
    }

    private async Task<Lookup> RunAsync(
        Flight flight, string key, Track seed, IReadOnlyList<IMetadataProvider> contributors, MetadataPolicy policy)
    {
        // Every RateGate this run queues in reads the priority from here.
        MetadataPriority.Current = flight.Ticket;
        var ct = flight.Cts.Token;
        var elapsed = Stopwatch.StartNew();
        MetadataTrace.BeginResolve(seed);
        try
        {
            // Ambiguous source titles attach alternative (artist, title) interpretations; try the
            // primary first, then candidates, keeping the one a catalog confirms.
            var lookup = seed.Candidates is { Count: > 0 }
                ? await LookupBestAsync(seed, contributors, policy, flight.Report, ct).ConfigureAwait(false)
                : await LookupOneAsync(Primary(seed), seed, contributors, policy, flight.Report, ct).ConfigureAwait(false);
            MetadataTrace.EndResolve(Finish(seed, lookup, policy).Track, elapsed.ElapsedMilliseconds);
            return lookup;
        }
        finally
        {
            lock (_lock)
            {
                if (_flights.TryGetValue(key, out var current) && current == flight) _flights.Remove(key);
                flight.Finished = true;
                flight.Dispose();
            }
        }
    }

    // Everything the lookup searches with. Two seeds with the same key ask the providers
    // the same questions, so they can share the answers.
    private static string FlightKey(Track seed, int generation)
    {
        const char sep = '\u001f';
        var key = $"{generation}{sep}{seed.SourceId}{sep}{seed.ExternalId}{sep}{seed.Title}{sep}{seed.Artist}{sep}{seed.Album}";
        if (seed.Candidates is { Count: > 0 } candidates)
            key += sep + string.Join(sep, candidates.Select(c => $"{c.Artist}{sep}{c.Title}"));
        return key;
    }

    // -- Interpretations --

    private static Interpretation Primary(Track seed) => new(seed.Title, seed.Artist, seed.ExternalId);

    // Try the primary interpretation and the candidates, returning the first that a catalog
    // confirms. Interpretations that carry an artist are tried first: a title-only match (empty
    // artist) is unreliable — it can latch onto a cover/remix of the same title — so it must not
    // win ahead of an artist-corroborated one. A clean "Artist - Title" still short-circuits on
    // its first (primary) lookup. If nothing matches, the primary's lookup stands (display =
    // primary parse + station-logo fallback).
    private async Task<Lookup> LookupBestAsync(
        Track seed, IReadOnlyList<IMetadataProvider> contributors, MetadataPolicy policy,
        Action<Lookup> progress, CancellationToken ct)
    {
        var primary = Primary(seed);
        var all = new List<Interpretation> { primary };
        foreach (var c in seed.Candidates!)
            if (!string.IsNullOrWhiteSpace(c.Title))
                all.Add(new Interpretation(c.Title, c.Artist ?? "", null));

        // A credit that names only the singing voice ("【Hatsune Miku】Migratory") says less than a
        // producer's ("Clean Tears") but more than none, so it goes between. A reading whose title
        // is a voice ("Melt" by "Hatsune Miku", reversed) isn't a song at all.
        var ordered = all.Where(i => !Voicebanks.IsVoicebankCredit(i.Title))
                         .OrderBy(i => string.IsNullOrWhiteSpace(i.Artist) ? 2 : Voicebanks.IsVoicebankCredit(i.Artist) ? 1 : 0);

        Lookup? primaryLookup = null;
        foreach (var interp in ordered)
        {
            // The first interpretation with any match is the one kept, so reporting its answers
            // early never shows an interpretation the full result then drops.
            var lookup = await LookupOneAsync(interp, seed, contributors, policy, progress, ct).ConfigureAwait(false);
            if (interp == primary) primaryLookup = lookup;
            if (lookup.Network.Count > 0) return lookup; // the confirmed interpretation
        }

        return primaryLookup ?? new Lookup(primary, new Dictionary<string, MetadataContribution>(StringComparer.Ordinal));
    }

    // Ask the contributors about one interpretation. Only non-empty contributions are kept:
    // contributors return one only when their own match guard passed, so any entry means a
    // catalog confirmed this interpretation. Each match is reported to progress with the
    // answers so far.
    private async Task<Lookup> LookupOneAsync(
        Interpretation interp, Track seed, IReadOnlyList<IMetadataProvider> contributors,
        MetadataPolicy policy, Action<Lookup> progress, CancellationToken ct)
    {
        var ordered = policy.Order
            .Where(id => id != MetadataPolicy.SourceId)
            .Select(id => contributors.FirstOrDefault(c => c.Id == id))
            .OfType<IMetadataProvider>()
            .ToList();

        MetadataTrace.BeginAttempt(interp.Artist, interp.Title, ordered.Select(c => c.Id));
        var network = new Dictionary<string, MetadataContribution>(StringComparer.Ordinal);

        if (!SearchTerms.IsArtistless(interp.Artist))
        {
            // Everyone searches the same query, so ask them all at once.
            var query = new MetadataQuery(seed.SourceId, interp.Title, interp.Artist, seed.Album, interp.ExternalId);
            await Task.WhenAll(ordered.Select(async contributor =>
            {
                if (await AskAsync(contributor, query, ct).ConfigureAwait(false) is not { } contribution) return;
                lock (network)
                {
                    network[contributor.Id] = contribution;
                    progress(new Lookup(interp, new Dictionary<string, MetadataContribution>(network, StringComparer.Ordinal)));
                }
            })).ConfigureAwait(false);
            return new Lookup(interp, network);
        }

        // No artist that can confirm a match: only a title, or the voice that sings it. Ask one at a
        // time, filling the working query's blanks from earlier answers so later searches have an
        // artist to confirm with. The catalogs that know which version is the original go first:
        // the first store to answer might have found a cover ("千本桜" by an artist named after a
        // lyric) or a remix album, and every later search would then confirm that.
        var artist = interp.Artist;
        var album = seed.Album;
        var weak = true;
        foreach (var contributor in ordered.OrderBy(c => c.IdentifiesOriginals ? 0 : 1))
        {
            var query = new MetadataQuery(seed.SourceId, interp.Title, artist, album, interp.ExternalId);
            if (await AskAsync(contributor, query, ct).ConfigureAwait(false) is not { } contribution) continue;
            network[contributor.Id] = contribution;
            progress(new Lookup(interp, new Dictionary<string, MetadataContribution>(network, StringComparer.Ordinal)));
            if (weak && !string.IsNullOrEmpty(contribution.Artist))
            {
                artist = contribution.Artist!;
                weak = false;
            }
            if (string.IsNullOrEmpty(album) && !string.IsNullOrEmpty(contribution.Album)) album = contribution.Album;
        }
        return new Lookup(interp, network);
    }

    // One contributor's answer, or null when it had nothing (or failed). Timed for the trace.
    private async Task<MetadataContribution?> AskAsync(
        IMetadataProvider contributor, MetadataQuery query, CancellationToken ct)
    {
        var elapsed = Stopwatch.StartNew();
        MetadataContribution? contribution;
        try
        {
            contribution = await contributor.ContributeAsync(query, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception ex)
        {
            // A provider that couldn't answer contributes nothing this time and cached nothing,
            // so the next play asks again. Say so where the user can see it.
            var reason = ex is CatalogUnavailableException ? ex.Message : $"{contributor.Id}: {ex.GetType().Name}: {ex.Message}";
            Log(reason);
            _log.Write("metadata", reason);
            MetadataTrace.Provider(contributor.Id, matched: false, null, null, null, 0, elapsed.ElapsedMilliseconds,
                error: ex is CatalogUnavailableException ? ex.Message : $"{ex.GetType().Name}: {ex.Message}");
            return null;
        }

        var matched = contribution is { IsEmpty: false };
        MetadataTrace.Provider(contributor.Id, matched,
            contribution?.Artist, contribution?.Title, contribution?.Album, contribution?.Art?.Length ?? 0,
            elapsed.ElapsedMilliseconds);
        return matched ? contribution : null;
    }

    // -- Merge --

    // Turn a (possibly shared) lookup into this caller's result: its seed, re-pointed at the
    // interpretation the lookup settled on, merged with what the providers said.
    private static MetadataResult Finish(Track seed, Lookup lookup, MetadataPolicy policy)
    {
        var interp = seed with
        {
            Title = lookup.Interp.Title,
            Artist = lookup.Interp.Artist,
            ExternalId = lookup.Interp.ExternalId,
            Candidates = null,
        };

        // A credit naming only the voice ("Hatsune Miku") isn't the song's artist: once a catalog
        // has found the song, show the artist it names (ryo's "Melt").
        var source = SourceContribution(interp);
        if (lookup.Network.Count > 0 && Voicebanks.IsVoicebankCredit(interp.Artist)) source = source with { Artist = null };
        var byId = new Dictionary<string, MetadataContribution>(lookup.Network, StringComparer.Ordinal)
        {
            [MetadataPolicy.SourceId] = source,
        };

        // Playable links any contributor learned (VocaDB PVs today), deduped by service+url, in
        // priority order — carried alongside the merged metadata for play history to replay from.
        var playables = policy.Order
            .Where(lookup.Network.ContainsKey)
            .Select(id => lookup.Network[id])
            .Where(c => c.Playables is { Count: > 0 })
            .SelectMany(c => c.Playables!)
            .DistinctBy(p => (p.Service, p.Url))
            .ToList();

        // A network contributor answered → a catalog matched this interpretation.
        return new MetadataResult(Merge(interp, byId, policy), lookup.Network.Count > 0, playables);
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
        lock (_lock)
        {
            owned = new List<IMetadataProvider>(_owned);
            _owned.Clear();
            _contributors = [];
            // Each run disposes its own flight when it unwinds.
            foreach (var f in _flights.Values) f.CancelIfRunning();
            _flights.Clear();
        }
        foreach (var c in owned) await DisposeQuietlyAsync(c).ConfigureAwait(false);
    }
}
