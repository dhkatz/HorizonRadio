using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Input;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using HorizonRadio.Core.Sources;
using HorizonRadio.UI.Imaging;
using HorizonRadio.UI.Services;

namespace HorizonRadio.UI.ViewModels;

/// <summary>One playable option behind a merged result row — a single source's hit plus
/// its display name, for the per-row source picker and labels.</summary>
public sealed record SourceOption(SearchResult Result, string SourceId, string DisplayName);

/// <summary>
/// One search-result row, shared by the top-bar live dropdown and the full search page.
/// Wraps a <see cref="MergedResult"/> — one track that one-or-more sources returned. The
/// row's display (title/subtitle/art) is stable (the merged display fields); the
/// <see cref="SelectedSource"/> only chooses which source actually plays, defaulting to
/// the user's highest-priority source present and overridable via the picker. Add / Play
/// route the selected source's hit through the shared <see cref="SearchEnqueuer"/>.
/// </summary>
public sealed partial class SearchResultRowViewModel : ViewModelBase
{
    private readonly MergedResult _merged;

    /// <summary>The sources that returned this track (encounter order), for labels + picker.</summary>
    public IReadOnlyList<SourceOption> Sources { get; }

    /// <summary>Show source labels on the row — only when more than one source is searchable.</summary>
    public bool ShowSourceLabels { get; }

    /// <summary>Show the per-row source picker — only when this row has more than one source.</summary>
    public bool HasMultipleSources => Sources.Count > 1;

    /// <summary>Which source plays on Add / Play. Defaults to the highest-priority source
    /// this row has; the picker overrides it.</summary>
    [ObservableProperty] private SourceOption selectedSource;

    public SearchResultRowViewModel(MergedResult merged, SearchEnqueuer enqueuer, SearchSourceContext context)
    {
        _merged = merged;
        ShowSourceLabels = context.ShowLabels;
        Sources = merged.Sources
            .Select(s => new SourceOption(s, s.SourceId, context.NameFor(s.SourceId)))
            .ToList();
        selectedSource = Sources.OrderBy(o => context.RankOf(o.SourceId)).First();

        AddCommand = new RelayCommand(() => _ = enqueuer.EnqueueAsync(SelectedSource.Result, playNow: false));
        PlayCommand = new RelayCommand(() => _ = enqueuer.EnqueueAsync(SelectedSource.Result, playNow: true));
    }

    public string Title => _merged.Title;
    public string Subtitle => _merged.Subtitle;
    public bool HasSubtitle => !string.IsNullOrWhiteSpace(_merged.Subtitle);

    /// <summary>Name of the source that will play — the picker button's label.</summary>
    public string SelectedSourceName => SelectedSource.DisplayName;

    /// <summary>Lazily-loaded thumbnail; null until <see cref="LoadArtAsync"/> resolves
    /// (or stays null on failure, showing the placeholder tile).</summary>
    [ObservableProperty] private Bitmap? art;

    public bool HasArt => Art != null;

    public ICommand AddCommand { get; }
    public ICommand PlayCommand { get; }

    /// <summary>Fetch + decode the artwork off the UI thread, then publish it on the UI
    /// thread (Avalonia bindings must be raised there). Safe to call once per row.</summary>
    public async Task LoadArtAsync(CancellationToken ct = default)
    {
        if (_merged.ArtUrl is null) return;
        var bmp = await RemoteArt.LoadAsync(_merged.ArtUrl, ct).ConfigureAwait(false);
        // Bail if a newer search superseded this one while we were fetching/decoding —
        // otherwise we'd marshal a decode onto a row already cleared from the list.
        if (bmp is null || ct.IsCancellationRequested) return;
        await Dispatcher.UIThread.InvokeAsync(() => Art = bmp);
    }

    partial void OnArtChanged(Bitmap? value) => OnPropertyChanged(nameof(HasArt));

    partial void OnSelectedSourceChanged(SourceOption value) => OnPropertyChanged(nameof(SelectedSourceName));
}
