using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using HorizonRadio.Core.Sources;
using HorizonRadio.UI.Services;

namespace HorizonRadio.UI.ViewModels;

/// <summary>
/// The full-width application top bar. Today it hosts the unified search box; the bar
/// is a persistent shell element (like the bottom player bar) so more global controls
/// can join it later. Typing runs a debounced live search and shows a handful of
/// results in a dropdown; pressing Enter / the search button hands the query to the
/// shell (<see cref="_onSubmit"/>) to open the full search page.
/// </summary>
public sealed partial class TopBarViewModel : ViewModelBase
{
    // One search fetches a page's worth; the dropdown shows the first few as a peek and
    // the full page reuses the SAME results (so the two surfaces can never disagree).
    // This is the count we'd LIKE — each source clamps to its own ceiling (e.g. Spotify
    // Dev Mode caps at 10), so we don't bake a per-source limit into the UI here.
    private const int SearchLimit = 30;
    private const int DropdownRows = 6;
    private static readonly TimeSpan DebounceDelay = TimeSpan.FromMilliseconds(250);

    private readonly SearchEnqueuer? _enqueuer;
    private readonly SearchSourceContext? _context;
    private readonly Action<string, UnifiedSearchResult>? _onSubmit;
    private CancellationTokenSource? _searchCts;

    // The last completed search, plus the query it's for — so Submit only reuses it when
    // it actually matches the query being submitted.
    private UnifiedSearchResult _lastResult = new([], []);
    private string _lastResultsQuery = "";

    /// <summary>The handful of live results under the box.</summary>
    public ObservableCollection<SearchResultRowViewModel> LiveResults { get; } = new();

    [ObservableProperty] private string query = "";

    /// <summary>Whether the live-results dropdown is shown. Open while a non-empty query
    /// is in flight or has results; light-dismissed by clicking away (the Popup).</summary>
    [ObservableProperty] private bool isDropdownOpen;

    [ObservableProperty] private bool isSearching;

    /// <summary>True once a search has run and returned nothing — drives the dropdown's
    /// "no results" line (distinct from the not-yet-searched empty state).</summary>
    [ObservableProperty] private bool hasNoResults;

    /// <summary>True when an empty result is because no search source is connected, not
    /// because the query matched nothing — drives a "connect a source" hint instead of
    /// a misleading "no results".</summary>
    [ObservableProperty] private bool needsConnection;

    public TopBarViewModel(SearchEnqueuer enqueuer, SearchSourceContext context, Action<string, UnifiedSearchResult> onSubmit)
    {
        _enqueuer = enqueuer;
        _context = context;
        _onSubmit = onSubmit;
    }

    /// <summary>Designer ctor — inert.</summary>
    public TopBarViewModel() { }

    partial void OnQueryChanged(string value)
    {
        _searchCts?.Cancel();

        if (string.IsNullOrWhiteSpace(value) || _enqueuer is null)
        {
            CloseDropdown();
            return;
        }

        var cts = _searchCts = new CancellationTokenSource();
        _ = RunLiveSearchAsync(value, cts.Token);
    }

    private async Task RunLiveSearchAsync(string query, CancellationToken ct)
    {
        try { await Task.Delay(DebounceDelay, ct); }
        catch (OperationCanceledException) { return; }

        IsSearching = true;
        IsDropdownOpen = true;
        HasNoResults = false;

        UnifiedSearchResult result;
        try { result = await UnifiedSearch.SearchAsync(query, SearchLimit, ct: ct); }
        catch (OperationCanceledException) { return; }
        catch { result = new UnifiedSearchResult([], []); }

        if (ct.IsCancellationRequested) return;

        _lastResult = result;
        _lastResultsQuery = query;

        LiveResults.Clear();
        foreach (var merged in SearchMerge.Merge(result.Results).Take(DropdownRows))
        {
            var row = new SearchResultRowViewModel(merged, _enqueuer!, _context!);
            LiveResults.Add(row);
            _ = row.LoadArtAsync(ct);
        }

        IsSearching = false;
        // Empty because nothing matched vs. because no source is connected — show the
        // right hint for each.
        NeedsConnection = result.Results.Count == 0 && !UnifiedSearch.HasReadySource;
        HasNoResults = result.Results.Count == 0 && !NeedsConnection;
    }

    /// <summary>Open the full search page for the current query (Enter / search button).
    /// No-op on an empty query.</summary>
    [RelayCommand]
    private void Submit()
    {
        var q = Query?.Trim();
        if (string.IsNullOrEmpty(q)) return;
        // Cancel any in-flight dropdown search and hand the page the results we already
        // have — but ONLY if they're for this exact query. If the user edited the query
        // and hit Enter before the new search landed, our cached results are stale, so
        // pass none and let the page run a fresh search for the submitted query.
        _searchCts?.Cancel();
        var result = string.Equals(_lastResultsQuery.Trim(), q, StringComparison.Ordinal)
            ? _lastResult
            : new UnifiedSearchResult([], []);
        CloseDropdown();
        _onSubmit?.Invoke(q, result);
    }

    /// <summary>Clear the box (the dropdown's not-now affordance / Escape).</summary>
    [RelayCommand]
    private void Clear()
    {
        Query = "";
        CloseDropdown();
    }

    private void CloseDropdown()
    {
        IsDropdownOpen = false;
        IsSearching = false;
        HasNoResults = false;
        NeedsConnection = false;
        LiveResults.Clear();
    }
}
