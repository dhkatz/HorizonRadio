using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using HorizonRadio.Core.Sources;
using HorizonRadio.UI.Services;

namespace HorizonRadio.UI.ViewModels;

/// <summary>
/// The full search page (a workspace, reached by submitting a query from the top bar —
/// it has no left-nav item, mirroring Spotify). Shows the complete result set for the
/// query, versus the top bar's handful-of-rows peek. Re-runnable: each
/// <see cref="RunAsync"/> replaces the results for a new query.
/// </summary>
public sealed partial class SearchViewModel : ViewModelBase
{
    // Fallback only (the page normally reuses the bar's results via Show). The count
    // we'd LIKE — each source clamps to its own ceiling (see SpotifySearch).
    private const int PageLimit = 30;

    private readonly SearchEnqueuer? _enqueuer;
    private CancellationTokenSource? _searchCts;

    public ObservableCollection<SearchResultRowViewModel> Results { get; } = new();

    /// <summary>The query these results are for (shown in the page header).</summary>
    [ObservableProperty] private string query = "";

    [ObservableProperty] private bool isSearching;

    /// <summary>True after a search completes with no hits — the "no results" state,
    /// distinct from the initial idle page.</summary>
    [ObservableProperty] private bool hasNoResults;

    /// <summary>True when the search itself failed (network/API error) — kept distinct
    /// from <see cref="HasNoResults"/> so a transient failure never reads as "no
    /// results" (which would falsely imply the query matched nothing).</summary>
    [ObservableProperty] private bool hasError;

    /// <summary>True when the empty result is because no search source is connected,
    /// not because the query matched nothing — drives a "connect a source" hint.</summary>
    [ObservableProperty] private bool needsConnection;

    public bool HasResults => Results.Count > 0;

    public SearchViewModel(SearchEnqueuer enqueuer) => _enqueuer = enqueuer;

    /// <summary>Designer ctor — inert.</summary>
    public SearchViewModel() { }

    /// <summary>Display results already fetched by the top-bar search (the normal path
    /// on submit). Reuses them rather than re-querying, so the page can't disagree with
    /// the dropdown or fail a second network call. Falls back to <see cref="RunAsync"/>
    /// only when the bar had nothing to hand over (e.g. submitted before results landed).</summary>
    public void Show(string searchQuery, IReadOnlyList<SearchResult> results)
    {
        if (results.Count == 0)
        {
            _ = RunAsync(searchQuery);
            return;
        }

        _searchCts?.Cancel();
        var cts = _searchCts = new CancellationTokenSource();
        Query = searchQuery;
        IsSearching = false;
        HasNoResults = false;
        HasError = false;
        NeedsConnection = false;
        Results.Clear();

        foreach (var r in results)
        {
            var row = new SearchResultRowViewModel(r, _enqueuer!);
            Results.Add(row);
            _ = row.LoadArtAsync(cts.Token);
        }
        OnPropertyChanged(nameof(HasResults));
    }

    /// <summary>Run (or re-run) the page for a query. Cancels any in-flight search.</summary>
    public async Task RunAsync(string searchQuery)
    {
        _searchCts?.Cancel();
        Query = searchQuery;

        if (string.IsNullOrWhiteSpace(searchQuery) || _enqueuer is null)
        {
            ResetResults();
            return;
        }

        var cts = _searchCts = new CancellationTokenSource();
        IsSearching = true;
        HasNoResults = false;
        HasError = false;
        NeedsConnection = false;
        Results.Clear();
        OnPropertyChanged(nameof(HasResults));

        IReadOnlyList<SearchResult> results;
        try { results = await UnifiedSearch.SearchAsync(searchQuery, PageLimit, cts.Token); }
        catch (System.OperationCanceledException) { return; }
        catch (System.Exception ex)
        {
            Debug.WriteLine($"[hzn-search] page search failed: {ex.Message}");
            if (cts.Token.IsCancellationRequested) return;
            IsSearching = false;
            HasError = true;
            return;
        }

        if (cts.Token.IsCancellationRequested) return;

        foreach (var r in results)
        {
            var row = new SearchResultRowViewModel(r, _enqueuer);
            Results.Add(row);
            _ = row.LoadArtAsync(cts.Token);
        }

        IsSearching = false;
        // Distinguish "nothing matched" from "no source connected" for the empty state.
        NeedsConnection = results.Count == 0 && !UnifiedSearch.HasReadySource;
        HasNoResults = results.Count == 0 && !NeedsConnection;
        OnPropertyChanged(nameof(HasResults));
    }

    private void ResetResults()
    {
        Results.Clear();
        IsSearching = false;
        HasNoResults = false;
        HasError = false;
        NeedsConnection = false;
        OnPropertyChanged(nameof(HasResults));
    }
}
