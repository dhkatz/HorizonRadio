namespace HorizonRadio.Core.Sources;

/// <summary>One searchable source's identity, for filter chips and per-row labels.</summary>
public sealed record SearchSourceInfo(string Id, string DisplayName);

/// <summary>What one source returned for a query: its hits and, if it failed, the error.
/// Lets the UI show "Spotify: error" beside another source's hits instead of a per-source
/// failure masquerading as "no results" (or sinking results another source did return).
/// <see cref="NotConnected"/> distinguishes a source that returned empty because its
/// account isn't connected (the source swallows that to an empty list by contract) from
/// one that genuinely matched nothing — so the UI can prompt to connect it.</summary>
public sealed record SourceSearchOutcome(
    string SourceId,
    string DisplayName,
    IReadOnlyList<SearchResult> Results,
    Exception? Error,
    bool NotConnected);

/// <summary>Result of a unified search: the flattened hits in source order, plus the
/// per-source breakdown so the UI can render partial failures and source labels.</summary>
public sealed record UnifiedSearchResult(
    IReadOnlyList<SearchResult> Results,
    IReadOnlyList<SourceSearchOutcome> Outcomes);

/// <summary>
/// Fans a free-text query out across every catalog source that implements
/// <see cref="ISearchSource"/> and gathers the hits. One place so both search surfaces
/// (the top-bar live dropdown and the full search page) query identically, differing only
/// by the per-source limit they ask for.
///
/// Per-source failures are captured (not thrown): a flaky or unconfigured source surfaces
/// as a <see cref="SourceSearchOutcome"/> with an <see cref="SourceSearchOutcome.Error"/>,
/// so it can't sink results another source returned, yet the failure is still visible.
/// The aggregator itself only throws on cancellation.
/// </summary>
public static class UnifiedSearch
{
    /// <summary>True when at least one source can be searched — lets the UI keep the
    /// search box inert (or hinting) when nothing is wired up yet.</summary>
    public static bool HasSearchableSource => SourceCatalog.All.OfType<ISearchSource>().Any();

    /// <summary>True when at least one search source is actually usable right now: it
    /// either needs no account, or its account is connected. Lets the UI tell "nothing
    /// matched" apart from "you're not connected".</summary>
    public static bool HasReadySource =>
        SourceCatalog.All.OfType<ISearchSource>()
            .Any(s => s is not IAuthenticatingSource auth || auth.IsConnected);

    /// <summary>The searchable sources, in catalog order — for filter chips and labels.</summary>
    public static IReadOnlyList<SearchSourceInfo> SearchableSources =>
        SourceCatalog.All
            .OfType<IAudioSourceFactory>()
            .Where(f => f is ISearchSource)
            .Select(f => new SearchSourceInfo(f.Id, f.DisplayName))
            .ToList();

    /// <param name="includeSourceIds">When non-null, only these source ids are queried
    /// (the filter chips). Null = all searchable sources.</param>
    public static async Task<UnifiedSearchResult> SearchAsync(
        string query, int limit, IReadOnlySet<string>? includeSourceIds = null, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(query))
            return new UnifiedSearchResult([], []);

        var sources = SourceCatalog.All
            .OfType<IAudioSourceFactory>()
            .Where(f => f is ISearchSource)
            .Where(f => includeSourceIds is null || includeSourceIds.Contains(f.Id))
            .ToList();
        if (sources.Count == 0) return new UnifiedSearchResult([], []);

        var outcomes = await Task.WhenAll(sources.Select(async factory =>
        {
            // An auth source that isn't connected returns [] by contract — flag it so the UI
            // can say "connect Spotify" rather than reading the empty list as "no matches".
            var notConnected = factory is IAuthenticatingSource auth && !auth.IsConnected;
            try
            {
                var results = await ((ISearchSource)factory).SearchAsync(query, limit, ct).ConfigureAwait(false);
                return new SourceSearchOutcome(factory.Id, factory.DisplayName, results, null, notConnected);
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[hzn-search] {factory.Id} failed: {ex.Message}");
                Diagnostics.ProcessConsole.Append("search", $"{factory.DisplayName} error: {ex.Message}");
                return new SourceSearchOutcome(factory.Id, factory.DisplayName, [], ex, notConnected);
            }
        })).ConfigureAwait(false);

        // Flatten in source order; the merge/interleave policy lives in SearchMerge, applied
        // by the UI so it can also label and pick per source.
        var merged = outcomes.SelectMany(o => o.Results).ToList();
        return new UnifiedSearchResult(merged, outcomes);
    }
}
