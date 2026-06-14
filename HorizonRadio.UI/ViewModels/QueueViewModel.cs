using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Input;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using HorizonRadio.Core.Metadata;
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
    private readonly MetadataResolver? _resolver;

    // Each distinct item is enriched at most once (lazy, cached, background); the
    // resolver's per-provider cache makes a repeat cheap, this avoids re-kicking.
    private readonly HashSet<string> _enriched = new();

    // Rolling window of upcoming rows we resolve full metadata + art for, ahead of
    // play. Re-evaluated on every rebuild, so as the queue advances later items enter
    // the window. Sized a little past a full-screen page of rows so a song moving
    // into now-playing doesn't reveal an un-enriched row popping in at the bottom.
    private const int EnrichLookahead = 18;

    // Cap concurrent metadata-only yt-dlp resolves so the window trickles in rather
    // than spawning a dozen processes at once. Caps the queue's enrichment to ≤3 at
    // a time (the Mixes tab has its own separate ≤3 enumerate cap).
    private static readonly SemaphoreSlim MetaGate = new(3, 3);

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

    // Now-playing enrichment state, keyed by a per-track signature rather than the
    // queue-item id — a radio station is ONE queue item reused across every song, so
    // its id never changes but its metadata does. `_nowSig` gates re-running the
    // resolve; the sticky `_nowEnrichedArt` (for `_nowEnrichedArtSig`) survives the
    // frequent rebuilds, which would otherwise reset the tile to the item's seed art
    // (the station icon).
    private string? _nowSig;
    private string? _nowEnrichedArtSig;
    private Bitmap? _nowEnrichedArt;

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

    public QueueViewModel(QueuePlayback queue, DialogManager? dialogs = null, MetadataResolver? resolver = null)
    {
        _queue = queue;
        _dialogs = dialogs;
        _resolver = resolver;
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
        // Prefer art enriched for this exact track; otherwise the item's own seed art
        // (e.g. the station logo before a song is identified, or an iTunes miss).
        NowPlayingArt = snap.Current is { } cur
            ? (NowSig(cur) == _nowEnrichedArtSig ? _nowEnrichedArt : null)
              ?? DecodeArtCached(cur.Id, cur.Metadata.AlbumArt)
            : null;

        SyncUpcoming(snap.Explicit);
        HasUpcoming = Upcoming.Count > 0;

        SyncContext(snap.ContextPeek);
        HasContext = ContextUpcoming.Count > 0;
        ContextHeader = snap.ContextName is { Length: > 0 } name ? $"Next From: {name}" : "Up Next";

        OnPropertyChanged(nameof(IsEmpty));

        if (snap.Current != null) EnrichCurrent(snap.Current);
        EnrichWindow(snap.Explicit);
    }

    // Resolve the rolling next-up window: full metadata (canonical title/artist via a
    // metadata-only resolve) + provider art, written back into the row. Once per item.
    private void EnrichWindow(IReadOnlyList<QueueItem> items)
    {
        var n = Math.Min(EnrichLookahead, items.Count);
        for (var i = 0; i < n; i++)
        {
            var item = items[i];
            if (!_enriched.Add(item.Id)) continue;
            var row = Upcoming.FirstOrDefault(r => r.Id == item.Id);
            if (row != null) _ = EnrichRowAsync(row, item);
        }
    }

    // Run the provider pass for the now-playing tile (square art + canonical text). Keyed
    // by a per-track signature so it re-runs when the song changes within a single, long-
    // lived queue item (radio) — not just when the item id changes. The resolver's cache
    // makes the repeat (already resolved at play time for the HUD) cheap.
    private void EnrichCurrent(QueueItem current)
    {
        if (_resolver is not { HasContributors: true }) return;
        var sig = NowSig(current);
        if (_nowSig == sig) return; // already enriched / in flight for this exact track
        _nowSig = sig;
        _ = Task.Run(async () =>
        {
            try
            {
                var enriched = await _resolver.ResolveAsync(current.Metadata, CancellationToken.None).ConfigureAwait(false);
                var art = enriched.AlbumArt is { Length: > 0 } ? DecodeArt(enriched.AlbumArt) : null;
                Dispatcher.UIThread.Post(() =>
                {
                    // Still the same track playing? (compare the signature, not the item —
                    // a radio item stays referentially equal across songs).
                    if (_queue?.Model.Current is not { } c || NowSig(c) != sig) return;
                    if (!string.IsNullOrWhiteSpace(enriched.Title)) NowPlayingTitle = enriched.Title;
                    NowPlayingSubtitle = enriched.Artist;
                    if (art != null)
                    {
                        _nowEnrichedArt = art;
                        _nowEnrichedArtSig = sig; // sticky, so the next rebuild keeps it
                        NowPlayingArt = art;
                    }
                });
            }
            catch (Exception ex) { Debug.WriteLine($"[hzn-queue-vm] enrich current: {ex.Message}"); }
        });
    }

    // Identifies a specific track within a queue item: id + the metadata that drives a
    // lookup. A radio station keeps one id but changes title/artist as songs roll over.
    private static string NowSig(QueueItem item) =>
        $"{item.Id}|{item.Metadata.Title}|{item.Metadata.Artist}";

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

    // The context peek is recomputed on every Changed (twice per track), but it
    // usually hasn't changed track-to-track. Skip the Clear+Add — which resets the
    // whole ItemsControl — when the rows are identical (QueuePreview is a record,
    // so this is structural equality).
    private void SyncContext(IReadOnlyList<QueuePreview> peek)
    {
        if (ContextUpcoming.SequenceEqual(peek)) return;
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

    // Upgrade an upcoming row: a metadata-only resolve gives canonical title/artist
    // (so providers can match → square art) plus a fallback thumbnail; the provider
    // pass then merges per the user's policy. Throttled and off the UI thread.
    private async Task EnrichRowAsync(QueueRowViewModel row, QueueItem item)
    {
        await MetaGate.WaitAsync().ConfigureAwait(false);
        try
        {
            var seed = item.Metadata;
            try
            {
                if (await item.Item.TryGetMetadataAsync(CancellationToken.None).ConfigureAwait(false) is { } better)
                    seed = better;
            }
            catch (Exception ex) { Debug.WriteLine($"[hzn-queue-vm] meta-ahead: {ex.Message}"); }

            var final = _resolver is { HasContributors: true }
                ? await _resolver.ResolveAsync(seed, CancellationToken.None).ConfigureAwait(false)
                : seed;

            var art = final.AlbumArt is { Length: > 0 } ? DecodeArt(final.AlbumArt) : null;
            Dispatcher.UIThread.Post(() =>
            {
                if (!string.IsNullOrWhiteSpace(final.Title)) row.Title = final.Title;
                if (!string.IsNullOrWhiteSpace(final.Artist)) row.Subtitle = final.Artist;
                if (art != null) row.Thumbnail = art;
            });
        }
        catch (Exception ex) { Debug.WriteLine($"[hzn-queue-vm] enrich row: {ex.Message}"); }
        finally { MetaGate.Release(); }
    }

    private Bitmap? DecodeArtCached(string id, byte[]? bytes)
    {
        if (_artCacheId == id && _artCacheBmp != null) return _artCacheBmp;
        _artCacheId = id;
        _artCacheBmp = DecodeArt(bytes);
        return _artCacheBmp;
    }

    private static Bitmap? DecodeArt(byte[]? bytes) => Imaging.ImageBytes.ToBitmap(bytes);

    partial void OnHasNowPlayingChanged(bool value) => OnPropertyChanged(nameof(IsEmpty));
    partial void OnHasUpcomingChanged(bool value) => OnPropertyChanged(nameof(IsEmpty));
    partial void OnHasContextChanged(bool value) => OnPropertyChanged(nameof(IsEmpty));

    /// <summary>Drag-and-drop reorder: move the dragged row to the dropped-on row's
    /// position. Called from the view's drop handler.</summary>
    public void ReorderTo(string sourceId, string targetId) =>
        _queue?.Model.MoveExplicitTo(sourceId, targetId);

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
/// play-now / remove / reorder commands. Title/Subtitle/Thumbnail are observable so
/// background metadata enrichment can write them back in place.</summary>
public sealed partial class QueueRowViewModel : ViewModelBase
{
    public string Id { get; }

    [ObservableProperty] private string title;
    [ObservableProperty] private string subtitle;
    [ObservableProperty] private Bitmap? thumbnail;

    public bool HasSubtitle => !string.IsNullOrWhiteSpace(Subtitle);
    public bool HasThumbnail => Thumbnail != null;

    public ICommand PlayNowCommand { get; }
    public ICommand RemoveCommand { get; }
    public ICommand MoveUpCommand { get; }
    public ICommand MoveDownCommand { get; }

    public QueueRowViewModel(
        string id, string title, string subtitle, Bitmap? thumbnail,
        Action<string> playNow, Action<string> remove,
        Action<string> moveUp, Action<string> moveDown)
    {
        Id = id;
        this.title = title;
        this.subtitle = subtitle;
        this.thumbnail = thumbnail;
        PlayNowCommand = new RelayCommand(() => playNow(id));
        RemoveCommand = new RelayCommand(() => remove(id));
        MoveUpCommand = new RelayCommand(() => moveUp(id));
        MoveDownCommand = new RelayCommand(() => moveDown(id));
    }

    partial void OnSubtitleChanged(string value) => OnPropertyChanged(nameof(HasSubtitle));
    partial void OnThumbnailChanged(Bitmap? value) => OnPropertyChanged(nameof(HasThumbnail));
}
