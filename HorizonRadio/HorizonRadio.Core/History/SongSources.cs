using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using HorizonRadio.Core.Metadata;
using HorizonRadio.Core.Sources;
using HorizonRadio.Sdk.Models;

namespace HorizonRadio.Core.History;

/// <summary>
/// Where a song with no playable origin of its own (a radio song) can be played: the services
/// that have it, found by searching its catalog-canonical name. Play history stores what this
/// finds per song; adding a radio song to the queue or a mix uses it to pick something to add.
/// </summary>
public static class SongSources
{
    // Per-source result ceiling for the search (Spotify dev apps cap at 10).
    private const int SearchLimit = 10;

    /// <summary>Search the services for an identified song and keep the hits that are it, at most
    /// one per service. <c>Ran</c> is false when no search source was ready or the search failed,
    /// so a caller can try again later rather than take "couldn't search" for "nowhere has it".</summary>
    public static async Task<(IReadOnlyList<ReplaySource> Sources, bool Ran)> SearchAsync(Track song, CancellationToken ct)
    {
        if (!UnifiedSearch.HasReadySource) return ([], false);
        var query = $"{song.Artist} {song.Title}".Trim();
        if (string.IsNullOrWhiteSpace(query)) return ([], true);

        UnifiedSearchResult result;
        try { result = await UnifiedSearch.SearchAsync(query, SearchLimit, ct: ct).ConfigureAwait(false); }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex) { Trace.WriteLine($"[hzn-song-sources] search: {ex.Message}"); return ([], false); }

        var sources = HistorySourceMatch.Select(song.Artist, song.Title, result.Results)
            .Select(r => new ReplaySource(r.SourceId, SourceCatalog.Find(r.SourceId)?.DisplayName ?? r.SourceId, r.Locator))
            .ToList();
        return (sources, true);
    }

    /// <summary>Identify a song in the catalogs, then find it: the catalog's own uploads first (the
    /// official videos VocaDB links), then a search of the services. Only a song the catalogs
    /// confirm is searched for, so a loosely parsed title can't pick another artist's song of the
    /// same name. Empty when it couldn't be identified or found.</summary>
    public static async Task<IReadOnlyList<ReplaySource>> FindAsync(MetadataResolver? resolver, Track seed, CancellationToken ct)
    {
        if (resolver is not { HasContributors: true }) return [];
        var resolved = await resolver.ResolveDetailedAsync(seed, ct).ConfigureAwait(false);
        if (!resolved.Matched) return [];
        var (hits, _) = await SearchAsync(resolved.Track, ct).ConfigureAwait(false);
        return HistorySourceMatch.Combine(HistorySourceMatch.FromPvs(resolved.Playables), hits);
    }
}
