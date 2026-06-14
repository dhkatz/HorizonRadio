namespace HorizonRadio.Core.Sources;

/// <summary>
/// Fans a free-text query out across every catalog source that implements
/// <see cref="ISearchSource"/> and merges the hits. One place so both search
/// surfaces (the top-bar live dropdown and the full search page) query identically,
/// differing only by the per-source <paramref name="limit"/> they ask for.
///
/// Per-source failures are swallowed to an empty list, so one unconfigured or flaky
/// source can't sink a query that spans several. Today only Spotify implements the
/// capability; YouTube/local generalize on the same seam with no change here.
/// </summary>
public static class UnifiedSearch
{
    /// <summary>True when at least one source can be searched — lets the UI keep the
    /// search box inert (or hinting) when nothing is wired up yet.</summary>
    public static bool HasSearchableSource => SourceCatalog.All.OfType<ISearchSource>().Any();

    /// <summary>True when at least one search source is actually usable right now: it
    /// either needs no account, or its account is connected. Lets the UI tell "nothing
    /// matched" apart from "you're not connected" — an unconnected source returns an
    /// empty list (by the <see cref="ISearchSource"/> contract), which would otherwise
    /// read as a false "no results".</summary>
    public static bool HasReadySource =>
        SourceCatalog.All.OfType<ISearchSource>()
            .Any(s => s is not IAuthenticatingSource auth || auth.IsConnected);

    public static async Task<IReadOnlyList<SearchResult>> SearchAsync(
        string query, int limit, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(query)) return [];

        var sources = SourceCatalog.All.OfType<ISearchSource>().ToList();
        if (sources.Count == 0) return [];

        var outcomes = await Task.WhenAll(sources.Select(async source =>
        {
            try { return (results: await source.SearchAsync(query, limit, ct).ConfigureAwait(false), error: (Exception?)null); }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[hzn-search] {source.GetType().Name} failed: {ex.Message}");
                Diagnostics.ProcessConsole.Append("search", $"{source.GetType().Name} error: {ex.Message}");
                return (results: (IReadOnlyList<SearchResult>)[], error: ex);
            }
        })).ConfigureAwait(false);

        // Flatten in source order. With one source today this is just its list; the
        // merge/interleave policy across sources is a later concern.
        var merged = outcomes.SelectMany(o => o.results).ToList();

        // If we got nothing AND a source actually errored, surface the failure rather
        // than letting it read as a (false) "no results" — the per-source swallow above
        // exists only so one flaky source can't sink results another source did return.
        if (merged.Count == 0 && outcomes.Select(o => o.error).FirstOrDefault(e => e is not null) is { } firstError)
            throw firstError;

        return merged;
    }
}
