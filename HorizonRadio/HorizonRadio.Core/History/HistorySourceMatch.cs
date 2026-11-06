using System;
using System.Collections.Generic;
using System.Linq;
using HorizonRadio.Core.Metadata;
using HorizonRadio.Core.Sources;

namespace HorizonRadio.Core.History;

/// <summary>
/// Picks the search hits that are actually the song we're looking for, so play history can store a
/// real playable URL per service. A unified search for "artist title" returns many tracks; this
/// keeps only the ones whose normalized tokens cover the (catalog-canonical) query — at most one
/// per service, the first (best-ranked) match — giving the multi-source set the replay picker uses.
///
/// Matching is deliberately conservative (the same token model as <see cref="SearchMerge"/>): the
/// query's tokens must be a subset of the result's, and the query must carry at least two tokens so
/// a one-word title can't latch onto an unrelated hit. Duration isn't considered — radio gives us
/// none — so this trusts the per-service search ranking to put the right version first.
/// </summary>
public static class HistorySourceMatch
{
    public static IReadOnlyList<SearchResult> Select(string artist, string title, IReadOnlyList<SearchResult> results)
    {
        var query = Tokenize($"{title} {artist}");
        if (query.Count < 2) return [];

        var bySource = new Dictionary<string, SearchResult>();
        var order = new List<string>();
        foreach (var r in results)
        {
            if (r.Kind != SearchResultKind.Track || bySource.ContainsKey(r.SourceId)) continue;
            if (query.IsSubsetOf(Tokenize($"{r.Title} {r.Subtitle}")))
            {
                bySource[r.SourceId] = r;
                order.Add(r.SourceId);
            }
        }
        return order.Select(s => bySource[s]).ToList();
    }

    private static HashSet<string> Tokenize(string s) =>
        SearchMerge.Normalize(s).Split(' ', StringSplitOptions.RemoveEmptyEntries).ToHashSet();

    /// <summary>Turn a resolved track's PV links into replay sources. They all route through the
    /// yt-dlp engine (the "youtube" content factory plays any yt-dlp-supported URL), but each keeps
    /// its real service as the picker label ("YouTube", "Niconico", …) — so a Niconico PV is offered
    /// and played without a separate Niconico source.</summary>
    // "youtube" is the yt-dlp content factory's source id (must match YouTubeSourceFactory.SourceId);
    // inlined so Core/History stays decoupled from the YouTube source assembly (see HistoryReplay).
    private const string YouTubeSourceId = "youtube";

    public static IReadOnlyList<ReplaySource> FromPvs(IReadOnlyList<PlayableRef> pvs) =>
        [.. pvs.Select(pv => new ReplaySource(YouTubeSourceId, pv.Service, pv.Url))];

    /// <summary>Combine the precise PV sources with name-search hits: PVs first, then any search hit
    /// for a service the PVs don't already cover (deduped by display, so a fuzzy YouTube search hit
    /// is dropped in favor of the exact YouTube PV while a Spotify hit is still added).</summary>
    public static IReadOnlyList<ReplaySource> Combine(IReadOnlyList<ReplaySource> pvs, IReadOnlyList<ReplaySource> searchHits)
    {
        var seen = new HashSet<string>(pvs.Select(s => s.SourceDisplay), StringComparer.OrdinalIgnoreCase);
        return [.. pvs, .. searchHits.Where(s => seen.Add(s.SourceDisplay))];
    }
}
