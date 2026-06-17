using System.Collections.Generic;
using System.Linq;
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
        SearchMerge.Normalize(s).Split(' ', System.StringSplitOptions.RemoveEmptyEntries).ToHashSet();
}
