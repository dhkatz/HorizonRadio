using System.Linq;
using HorizonRadio.Core.Metadata;
using HorizonRadio.Core.Sources;
using HorizonRadio.Sdk;
using HorizonRadio.Sdk.Metadata;
using HorizonRadio.Sdk.Models;

namespace HorizonRadio.Core.History;

/// <summary>
/// Works out what the songs in the history are when nothing can replay them yet (a radio song:
/// the station's title), and where they can be played again. Each is looked up in the catalogs
/// (<see cref="MetadataResolver"/>, behind the song playing now); one a catalog confirms gets its
/// official uploads and the services' search hits as replay sources, and one none confirms is
/// marked <see cref="HistoryMatchState.Unmatched"/>, which the History page offers to report.
///
/// It runs with the engine, whatever shows the history: each such song once a session, a few at a
/// time, plus new ones as they're played. A song whose search couldn't run yet (no search source
/// signed in) is tried again on <see cref="Retry"/>.
/// </summary>
public sealed class HistoryIdentifier : IDisposable
{
    private const int AtOnce = 2;

    private readonly PlayHistoryStore _store;
    private readonly MetadataResolver _resolver;
    private readonly SourceCatalog _sources;
    private readonly IPluginLog _log;
    private readonly SemaphoreSlim _gate = new(AtOnce, AtOnce);
    private readonly CancellationTokenSource _stop = new();
    private readonly Lock _lock = new();
    private readonly HashSet<string> _tried = new(StringComparer.Ordinal);
    private int _running;
    private bool _started;

    public HistoryIdentifier(PlayHistoryStore store, MetadataResolver resolver, SourceCatalog sources, IPluginLog log)
    {
        _store = store;
        _resolver = resolver;
        _sources = sources;
        _log = log;
    }

    /// <summary>Raised when no lookup is running any more (after a pass, or a retry). On the thread
    /// that finished the last one.</summary>
    public event Action? Idle;

    /// <summary>Start: look up the history now, and each song added to it.</summary>
    public void Start()
    {
        lock (_lock)
        {
            if (_started) return;
            _started = true;
        }
        _store.Changed += Pass;
        Pass();
    }

    /// <summary>Look up again the songs that couldn't be settled (a search source has signed in
    /// since, say). The History page calls it when it's shown.</summary>
    public void Retry() => Pass();

    private void Pass()
    {
        if (_stop.IsCancellationRequested || !_resolver.HasContributors) return;
        List<PlayHistoryEntry> due;
        lock (_lock)
        {
            due = [.. _store.All.Where(e => e.Sources.Count == 0 && _tried.Add(e.Id))];
            _running += due.Count;
        }
        foreach (var entry in due) _ = IdentifyAsync(entry);
    }

    private async Task IdentifyAsync(PlayHistoryEntry entry)
    {
        var settled = false;
        try
        {
            await _gate.WaitAsync(_stop.Token).ConfigureAwait(false);
            try { settled = await LookUpAsync(entry, _stop.Token).ConfigureAwait(false); }
            finally { _gate.Release(); }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            _log.Trace("history", $"identify {entry.Artist} - {entry.Title}: {ex.Message}");
        }
        finally
        {
            bool idle;
            lock (_lock)
            {
                if (!settled) _tried.Remove(entry.Id); // a later pass tries it again
                idle = --_running == 0;
            }
            if (idle) Idle?.Invoke();
        }
    }

    // True when nothing more will come of looking it up again this session.
    private async Task<bool> LookUpAsync(PlayHistoryEntry entry, CancellationToken ct)
    {
        // The song playing now goes ahead of these at every provider.
        var resolved = await _resolver.ResolveDetailedAsync(SeedOf(entry), ct, ResolvePriority.Background)
            .ConfigureAwait(false);
        if (!resolved.Matched)
        {
            // No catalog could confirm what it is: the History page offers to report it.
            _store.SetMatchState(entry.Id, HistoryMatchState.Unmatched);
            return true;
        }

        // The catalog confirmed it. Its official uploads (VocaDB's PVs, Niconico among them) come
        // first, then any service's search hit for the confirmed name. Searching only once it's
        // confirmed keeps a title-only guess from taking another artist's song of the same name.
        var official = HistorySourceMatch.FromPvs(resolved.Playables);
        var (hits, ran) = await SongSources.SearchAsync(_sources, resolved.Track, ct).ConfigureAwait(false);
        var found = HistorySourceMatch.Combine(official, hits);
        if (found.Count > 0) _store.SetSources(entry.Id, found);
        _store.SetMatchState(entry.Id, HistoryMatchState.Matched);
        _log.Trace("history", $"identified {entry.Artist} - {entry.Title}: {found.Count} source(s)");
        // A search source that wasn't ready (not signed in yet) could still add one later; having
        // an official upload mustn't stop the other services from being searched.
        return ran || (official.Count > 0 && !_sources.Search.HasSearchableSource);
    }

    /// <summary>What a stored song is looked up as: its title, artist and the station's other
    /// readings of its title.</summary>
    public static Track SeedOf(PlayHistoryEntry e)
    {
        var candidates = e.Candidates.Count > 0
            ? e.Candidates.Select(c => new TitleCandidate(c.Artist, c.Title)).ToList()
            : null;
        return new Track(e.Title, e.Artist, e.Album, null, e.SourceId, e.SourceDisplay, Year: e.Year,
            Candidates: candidates);
    }

    public void Dispose()
    {
        _store.Changed -= Pass;
        _stop.Cancel();
    }
}
