using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Linq;
using System.Threading.Tasks;
using System.Windows.Input;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using HorizonRadio.Core.Sources;
using HorizonRadio.Core.Sources.Queue;
using ShadUI;

namespace HorizonRadio.UI.ViewModels;

/// <summary>
/// The toggleable right-hand queue sidebar: a Spotify-style view of one global
/// queue. Mirrors the <see cref="QueueModel"/> (the single source of truth driving
/// playback) into bound collections — the track playing now, the explicit "next in
/// queue" one-offs, and a rolling peek of upcoming tracks from the active mix
/// context ("next from: …"). The + button adds a one-off via the same quick-play
/// dialog the player bar uses; rows carry their own play-now/remove/reorder commands.
/// </summary>
public sealed partial class QueueViewModel : ViewModelBase
{
    private readonly QueuePlayback? _queue;
    private readonly DialogManager? _dialogs;

    /// <summary>Now-playing line at the top of the sidebar.</summary>
    [ObservableProperty] private string nowPlayingTitle = "";
    [ObservableProperty] private string nowPlayingSubtitle = "";
    [ObservableProperty] private bool hasNowPlaying;
    [ObservableProperty] private Bitmap? nowPlayingArt;

    // Cache the decoded now-playing art so the frequent Changed/rebuilds don't
    // re-decode the same bytes; keyed by queue-item id (art may arrive on a later
    // rebuild once the track prepares, so re-decode while the cached art is null).
    private string? _artCacheId;
    private Bitmap? _artCacheBmp;

    /// <summary>The explicit "next in queue" zone (user one-offs).</summary>
    public ObservableCollection<QueueRowViewModel> Upcoming { get; } = new();

    /// <summary>A rolling window of what's coming from the active mix context.</summary>
    public ObservableCollection<QueuePreview> ContextUpcoming { get; } = new();

    [ObservableProperty] private bool hasUpcoming;
    [ObservableProperty] private bool hasContext;
    [ObservableProperty] private string contextHeader = "";

    /// <summary>True when there's nothing playing and nothing queued — drives the
    /// empty-state hint.</summary>
    public bool IsEmpty => !HasNowPlaying && !HasUpcoming && !HasContext;

    /// <summary>Content sources the + button can add a one-off from (a mix/queue
    /// can't hold a self-driven source like Spotify Connect).</summary>
    public IReadOnlyList<IAudioSourceFactory> ContentSources { get; }

    /// <summary>Bound to the + button's source flyout. Picking a source opens the
    /// quick-play dialog for it; we reset to null so re-picking the same one
    /// re-fires (a launcher, not a persistent selection — like the player bar).</summary>
    [ObservableProperty] private IAudioSourceFactory? selectedAddSource;
    private bool _suppressAdd;

    public QueueViewModel(QueuePlayback queue, DialogManager? dialogs = null)
    {
        _queue = queue;
        _dialogs = dialogs;
        ContentSources = SourceCatalog.All.Where(f => f is IContentSourceFactory).ToList();

        _queue.Model.Changed += OnModelChanged;
        Rebuild();
    }

    /// <summary>Designer ctor — inert.</summary>
    public QueueViewModel()
    {
        ContentSources = SourceCatalog.All.Where(f => f is IContentSourceFactory).ToList();
    }

    private void OnModelChanged() => Dispatcher.UIThread.Post(Rebuild);

    private void Rebuild()
    {
        if (_queue == null) return;
        var snap = _queue.Model.Snapshot();

        HasNowPlaying = snap.Current != null;
        NowPlayingTitle = snap.Current?.Metadata.Title is { Length: > 0 } t ? t : "";
        NowPlayingSubtitle = snap.Current?.Metadata.Artist ?? "";
        NowPlayingArt = snap.Current != null
            ? DecodeArtCached(snap.Current.Id, snap.Current.Metadata.AlbumArt)
            : null;

        SyncUpcoming(snap.Explicit);
        HasUpcoming = Upcoming.Count > 0;

        SyncContext(snap.ContextPeek);
        HasContext = ContextUpcoming.Count > 0;
        ContextHeader = snap.ContextName is { Length: > 0 } name ? $"Next From: {name}" : "Up Next";

        OnPropertyChanged(nameof(IsEmpty));
    }

    // In-place sync by id so a track change (which doesn't touch the explicit zone)
    // doesn't tear down and rebuild every row.
    private void SyncUpcoming(IReadOnlyList<QueueItem> items)
    {
        for (var i = Upcoming.Count - 1; i >= 0; i--)
            if (!items.Any(q => q.Id == Upcoming[i].Id)) Upcoming.RemoveAt(i);

        for (var i = 0; i < items.Count; i++)
        {
            if (i < Upcoming.Count && Upcoming[i].Id == items[i].Id) continue;
            var existing = Upcoming.FirstOrDefault(r => r.Id == items[i].Id);
            if (existing != null) Upcoming.Move(Upcoming.IndexOf(existing), i);
            else Upcoming.Insert(i, CreateRow(items[i]));
        }
    }

    private void SyncContext(IReadOnlyList<QueuePreview> peek)
    {
        ContextUpcoming.Clear();
        foreach (var p in peek) ContextUpcoming.Add(p);
    }

    private QueueRowViewModel CreateRow(QueueItem item) => new(
        item.Id,
        string.IsNullOrWhiteSpace(item.Metadata.Title) ? "Unknown track" : item.Metadata.Title,
        item.Metadata.Artist,
        DecodeArt(item.Metadata.AlbumArt),
        playNow: id => _queue?.Model.JumpToExplicit(id),
        remove: id => _queue?.Model.RemoveExplicit(id),
        moveUp: id => _queue?.Model.MoveExplicit(id, -1),
        moveDown: id => _queue?.Model.MoveExplicit(id, +1));

    private Bitmap? DecodeArtCached(string id, byte[]? bytes)
    {
        if (_artCacheId == id && _artCacheBmp != null) return _artCacheBmp;
        _artCacheId = id;
        _artCacheBmp = DecodeArt(bytes);
        return _artCacheBmp;
    }

    private static Bitmap? DecodeArt(byte[]? bytes)
    {
        if (bytes == null || bytes.Length == 0) return null;
        try
        {
            using var ms = new System.IO.MemoryStream(bytes);
            return new Bitmap(ms);
        }
        catch
        {
            return null; // malformed art — show the placeholder tile instead
        }
    }

    partial void OnHasNowPlayingChanged(bool value) => OnPropertyChanged(nameof(IsEmpty));
    partial void OnHasUpcomingChanged(bool value) => OnPropertyChanged(nameof(IsEmpty));
    partial void OnHasContextChanged(bool value) => OnPropertyChanged(nameof(IsEmpty));

    [RelayCommand]
    private void ClearQueue() => _queue?.Model.ClearExplicit();

    // Picking a source from the + flyout launches its quick-play dialog. Reset the
    // selection (suppressed) so picking the same source again re-opens the dialog.
    partial void OnSelectedAddSourceChanged(IAudioSourceFactory? value)
    {
        if (_suppressAdd || value == null) return;
        var picked = value;
        _suppressAdd = true;
        SelectedAddSource = null;
        _suppressAdd = false;
        Add(picked);
    }

    /// <summary>Add a one-off from a chosen content source — opens the same
    /// quick-play dialog the player bar uses, then enqueues on success.</summary>
    private void Add(IAudioSourceFactory? source)
    {
        if (source == null || _dialogs == null || _queue == null) return;
        var dialog = new QuickPlayDialogViewModel(_dialogs, source);
        _dialogs.CreateDialog(dialog)
            .Dismissible()
            .WithSuccessCallback(vm => _ = EnqueueAsync(source, vm.Locator))
            .Show();
    }

    private async Task EnqueueAsync(IAudioSourceFactory source, string locator)
    {
        if (_queue == null) return;
        try
        {
            await _queue.EnqueueLocatorAsync(source, locator);
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[hzn-queue-vm] enqueue failed: {ex.Message}");
        }
    }
}

/// <summary>A row in the explicit "next in queue" zone, carrying its own
/// play-now / remove / reorder commands (closing over the queue model).</summary>
public sealed class QueueRowViewModel(
    string id,
    string title,
    string subtitle,
    Bitmap? thumbnail,
    Action<string> playNow,
    Action<string> remove,
    Action<string> moveUp,
    Action<string> moveDown)
{
    public string Id { get; } = id;
    public string Title { get; } = title;
    public string Subtitle { get; } = subtitle;
    public bool HasSubtitle => !string.IsNullOrWhiteSpace(Subtitle);

    /// <summary>Album art if it's already resolved (usually only after the item has
    /// played); otherwise null and the view shows a music-note placeholder tile.</summary>
    public Bitmap? Thumbnail { get; } = thumbnail;
    public bool HasThumbnail => Thumbnail != null;

    public ICommand PlayNowCommand { get; } = new RelayCommand(() => playNow(id));
    public ICommand RemoveCommand { get; } = new RelayCommand(() => remove(id));
    public ICommand MoveUpCommand { get; } = new RelayCommand(() => moveUp(id));
    public ICommand MoveDownCommand { get; } = new RelayCommand(() => moveDown(id));
}
