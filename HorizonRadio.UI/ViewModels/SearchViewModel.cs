using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using HorizonRadio.Core.Sources;
using HorizonRadio.UI.Services;

namespace HorizonRadio.UI.ViewModels;

/// <summary>
/// The full search page (a workspace, reached by submitting a query from the top bar —
/// it has no left-nav item, mirroring Spotify). Shows the complete result set for the
/// query, merged across sources, with per-source filter chips and a per-source failure
/// notice. Re-runnable: each run replaces the results for a new query (or a chip change).
/// </summary>
public sealed partial class SearchViewModel : ViewModelBase, IDisposable
{
    // Fallback only (the page normally reuses the bar's results via Show). The count
    // we'd LIKE — each source clamps to its own ceiling (see SpotifySearch).
    private const int PageLimit = 30;

    private readonly SearchEnqueuer? _enqueuer;
    private readonly SearchSourceContext? _context;
    private CancellationTokenSource? _searchCts;

    public ObservableCollection<SearchResultRowViewModel> Results { get; } = new();

    /// <summary>Per-source filter chips (only shown when more than one source is searchable).</summary>
    public ObservableCollection<SourceFilterChipViewModel> Filters { get; } = new();

    /// <summary>Per-source problem notices ("Spotify: not connected") shown beside results
    /// so a source failing/disconnected can't masquerade as "no results".</summary>
    public ObservableCollection<string> SourceNotices { get; } = new();

    public bool ShowFilters => _context?.ShowLabels == true && Filters.Count > 0;
    public bool HasSourceNotices => SourceNotices.Count > 0;

    /// <summary>The query these results are for (shown in the page header).</summary>
    [ObservableProperty] private string query = "";

    [ObservableProperty] private bool isSearching;

    /// <summary>True after a search completes with no hits — the "no results" state,
    /// distinct from the initial idle page.</summary>
    [ObservableProperty] private bool hasNoResults;

    /// <summary>True when every queried source failed — kept distinct from
    /// <see cref="HasNoResults"/> so a transient failure never reads as "no results".</summary>
    [ObservableProperty] private bool hasError;

    /// <summary>True when the empty result is because no search source is connected.</summary>
    [ObservableProperty] private bool needsConnection;

    public bool HasResults => Results.Count > 0;

    public SearchViewModel(SearchEnqueuer enqueuer, SearchSourceContext context)
    {
        _enqueuer = enqueuer;
        _context = context;
        foreach (var s in UnifiedSearch.SearchableSources)
            Filters.Add(new SourceFilterChipViewModel(s.Id, s.DisplayName, OnFilterChanged));
    }

    /// <summary>Designer ctor — inert.</summary>
    public SearchViewModel() { }

    /// <summary>Display a result set already fetched by the top-bar search (the normal path
    /// on submit). Reuses it rather than re-querying, so the page can't disagree with the
    /// dropdown. Falls back to <see cref="RunAsync"/> when the bar had nothing to hand over.</summary>
    public void Show(string searchQuery, UnifiedSearchResult result)
    {
        if (result.Outcomes.Count == 0 && result.Results.Count == 0)
        {
            _ = RunAsync(searchQuery);
            return;
        }

        _searchCts?.Cancel();
        var cts = _searchCts = new CancellationTokenSource();
        Query = searchQuery;
        IsSearching = false;
        ResetFilters();
        Render(result, cts.Token);
    }

    /// <summary>Run (or re-run) the page for a query, scoped to the enabled filter chips.
    /// Cancels any in-flight search.</summary>
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
        ClearStates();
        Results.Clear();
        OnPropertyChanged(nameof(HasResults));

        UnifiedSearchResult result;
        try { result = await UnifiedSearch.SearchAsync(searchQuery, PageLimit, IncludeSet(), cts.Token); }
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
        IsSearching = false;
        Render(result, cts.Token);
    }

    private void Render(UnifiedSearchResult result, CancellationToken ct)
    {
        Results.Clear();
        foreach (var merged in SearchMerge.Merge(result.Results))
        {
            var row = new SearchResultRowViewModel(merged, _enqueuer!, _context!);
            Results.Add(row);
            _ = row.LoadArtAsync(ct);
        }

        SourceNotices.Clear();
        foreach (var o in result.Outcomes)
        {
            if (o.Error is not null) SourceNotices.Add($"{o.DisplayName}: search failed");
            else if (o.NotConnected) SourceNotices.Add($"{o.DisplayName}: not connected");
        }
        OnPropertyChanged(nameof(HasSourceNotices));

        // Empty-state logic: all queried sources errored → error; no source ready at all →
        // connect; otherwise nothing matched. A partial failure (some results, some errors)
        // shows the results plus the per-source notice above.
        var allErrored = result.Outcomes.Count > 0 && result.Outcomes.All(o => o.Error is not null);
        NeedsConnection = result.Results.Count == 0 && !UnifiedSearch.HasReadySource;
        HasError = result.Results.Count == 0 && allErrored;
        HasNoResults = result.Results.Count == 0 && !NeedsConnection && !HasError;
        OnPropertyChanged(nameof(HasResults));
    }

    private void OnFilterChanged() => _ = RunAsync(Query);

    // All chips enabled → null (query everything); otherwise only the enabled ids.
    private HashSet<string>? IncludeSet()
    {
        var enabled = Filters.Where(f => f.IsEnabled).Select(f => f.Id).ToHashSet();
        return enabled.Count == Filters.Count ? null : enabled;
    }

    private void ResetFilters()
    {
        foreach (var f in Filters) f.IsEnabled = true;
    }

    private void ClearStates()
    {
        HasNoResults = false;
        HasError = false;
        NeedsConnection = false;
        SourceNotices.Clear();
        OnPropertyChanged(nameof(HasSourceNotices));
    }

    private void ResetResults()
    {
        Results.Clear();
        IsSearching = false;
        ClearStates();
        OnPropertyChanged(nameof(HasResults));
    }

    public void Dispose()
    {
        _searchCts?.Cancel();
        _searchCts?.Dispose();
    }
}
