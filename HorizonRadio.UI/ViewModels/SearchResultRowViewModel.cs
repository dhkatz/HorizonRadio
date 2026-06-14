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

/// <summary>
/// One search-result row, shared by the top-bar live dropdown and the full search
/// page. Carries display fields plus Add / Play commands that route through the shared
/// <see cref="SearchEnqueuer"/>. Art is loaded lazily from the result's URL (see
/// <see cref="LoadArtAsync"/>) and written back to the observable <see cref="Art"/>.
/// </summary>
public sealed partial class SearchResultRowViewModel : ViewModelBase
{
    private readonly SearchResult _result;

    public SearchResultRowViewModel(SearchResult result, SearchEnqueuer enqueuer)
    {
        _result = result;
        AddCommand = new RelayCommand(() => _ = enqueuer.EnqueueAsync(result, playNow: false));
        PlayCommand = new RelayCommand(() => _ = enqueuer.EnqueueAsync(result, playNow: true));
    }

    public string Title => _result.Title;
    public string Subtitle => _result.Subtitle;
    public bool HasSubtitle => !string.IsNullOrWhiteSpace(_result.Subtitle);

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
        if (_result.ArtUrl is null) return;
        var bmp = await RemoteArt.LoadAsync(_result.ArtUrl, ct).ConfigureAwait(false);
        if (bmp != null)
            await Dispatcher.UIThread.InvokeAsync(() => Art = bmp);
    }

    partial void OnArtChanged(Bitmap? value) => OnPropertyChanged(nameof(HasArt));
}
