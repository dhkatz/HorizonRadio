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
using HorizonRadio.Core.Sources.Queue;
using HorizonRadio.Sdk;
using HorizonRadio.Sdk.Metadata;
using HorizonRadio.Sdk.Models;
using HorizonRadio.Sdk.Sources;
using ShadUI;

namespace HorizonRadio.UI.ViewModels;

/// <summary>
/// The toggleable right-hand queue sidebar: a Spotify-style view of one global
/// queue. Mirrors the <see cref="QueueModel"/> (the single source of truth driving
/// playback) into bound collections — the track playing now, the explicit "next in
/// queue" one-offs, and a rolling peek of upcoming tracks from the active mix
/// context ("next from: …"). The + button adds a one-off via the same quick-play
/// dialog the player bar uses; rows carry their own play-now/remove/reorder commands.
/// In a listening party (as a guest) it's the party's queue, the host's: the same actions go to the
/// host.
/// </summary>
public sealed partial class QueueViewModel : ViewModelBase, IDisposable
{
    private readonly QueuePlayback? _queue;
    private readonly DialogManager? _dialogs;
    private readonly MetadataResolver? _resolver;
    private readonly SongActions? _songs;

    // Each distinct item is enriched at most once (lazy, cached, background); the
    // resolver's per-provider cache makes a repeat cheap, this avoids re-kicking.
    private readonly HashSet<string> _enriched = new();

    // Rolling window of upcoming rows we resolve full metadata + art for, ahead of
    // play. Re-evaluated on every rebuild, so as the queue advances later items enter
    // the window. Sized a little past a full-screen page of rows so a song moving
    // into now-playing doesn't reveal an un-enriched row popping in at the bottom.
    private const int EnrichLookahead = 18;

    // Cap concurrent metadata-only yt-dlp resolves so the window trickles in rather than spawning
    // a dozen processes at once. Shared process-wide (see EnrichmentThrottle) so the queue and the
    // History tab enriching at the same time can't together exceed the cap.
    private static SemaphoreSlim MetaGate => EnrichmentThrottle.Gate;

    /// <summary>Now-playing line at the top of the sidebar.</summary>
    [ObservableProperty] private string nowPlayingTitle = "";
    [ObservableProperty] private string nowPlayingSubtitle = "";
    [ObservableProperty] private bool hasNowPlaying;
    [ObservableProperty] private Bitmap? nowPlayingArt;

    /// <summary>The song playing now, for the now-playing card's right-click menu.</summary>
    [ObservableProperty] private SongTarget? nowPlayingSong;

    // Cache the decoded now-playing seed art so the frequent Changed/rebuilds don't
    // re-decode the same bytes. Keyed by the bytes themselves, not the queue item: a radio
    // station is one item across every song, and each song can bring its own art.
    private byte[]? _artCacheBytes;
    private Bitmap? _artCacheBmp;

    // Now-playing enrichment state, keyed by a per-track signature rather than the
    // queue-item id — a radio station is ONE queue item reused across every song, so
    // its id never changes but its metadata does. `_nowSig` gates re-running the
    // resolve (and `_nowCts` cancels the lookup for the previous signature); the enriched
    // text and art (for `_nowEnrichedSig`) survive the frequent rebuilds, which would
    // otherwise reset the tile to the item's raw title and seed art (the station icon).
    private string? _nowSig;
    private CancellationTokenSource? _nowCts;
    private string? _nowEnrichedSig;
    private string? _nowEnrichedTitle;
    private string? _nowEnrichedArtist;
    private Bitmap? _nowEnrichedArt;

    /// <summary>The explicit "next in queue" zone (user one-offs).</summary>
    public ObservableCollection<QueueRowViewModel> Upcoming { get; } = new();

    /// <summary>A rolling window of what's coming from the active mix context.</summary>
    public ObservableCollection<QueuePreviewRow> ContextUpcoming { get; } = new();

    [ObservableProperty] private bool hasUpcoming;
    [ObservableProperty] private bool hasContext;
    [ObservableProperty] private string contextHeader = "";

    /// <summary>It's a listening party's queue (the host's), which everyone in it can change.</summary>
    [ObservableProperty] private bool isShared;

    public string Title => IsShared ? "Party Queue" : "Queue";

    public string EmptyText => IsShared
        ? "The party's queue is empty. Add a track with the + button."
        : "Your queue is empty. Play a mix or add a track with the + button.";

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
    private readonly IConfirmations _confirm = ProceedWithoutAsking.Instance;

    public QueueViewModel(QueuePlayback queue, DialogManager? dialogs = null, MetadataResolver? resolver = null,
        SongActions? songs = null, IConfirmations? confirmations = null)
    {
        _queue = queue;
        _confirm = confirmations ?? ProceedWithoutAsking.Instance;
        _dialogs = dialogs;
        _resolver = resolver;
        _songs = songs;
        ContentSources = queue.Sources.All.Where(f => f is IContentSourceFactory).ToList();

        _queue.Model.Changed += OnModelChanged;
        Rebuild();
    }

    /// <summary>Designer ctor — inert.</summary>
    public QueueViewModel()
    {
        ContentSources = [];
    }

    private void OnModelChanged() => Dispatcher.UIThread.Post(Rebuild);

    public void Dispose()
    {
        if (_queue != null) _queue.Model.Changed -= OnModelChanged;
        _nowCts?.Cancel();
        _nowCts?.Dispose();
        _nowCts = null;
    }

    private void Rebuild()
    {
        if (_queue == null) return;
        var snap = _queue.Model.Snapshot();

        IsShared = snap.Shared;
        HasNowPlaying = snap.Current != null;
        // Prefer what was enriched for this exact track; otherwise the item's own seed
        // (e.g. the raw stream title and station logo before a song is identified).
        var enriched = snap.Current is { } playing && NowSig(playing) == _nowEnrichedSig;
        NowPlayingTitle = enriched && _nowEnrichedTitle is { Length: > 0 } et ? et
            : snap.Current?.Metadata.Title is { Length: > 0 } t ? t : "";
        NowPlayingSubtitle = (enriched ? _nowEnrichedArtist : null) ?? snap.Current?.Metadata.Artist ?? "";
        NowPlayingArt = snap.Current is { } cur
            ? (enriched ? _nowEnrichedArt : null) ?? DecodeArtCached(cur.Metadata.AlbumArt)
            : null;
        var song = snap.Current is { } now ? Song.FromTrack(now.Metadata) : null;
        if (song != NowPlayingSong?.Song) NowPlayingSong = _songs?.For(song);

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

        // Leave the previous track's lookup, so it stops if nobody else wants it. Disposing
        // right after cancelling is safe: its token stays cancelled for the task still using it.
        _nowCts?.Cancel();
        _nowCts?.Dispose();
        _nowCts = new CancellationTokenSource();
        var ct = _nowCts.Token;
        var seed = current.Metadata;
        _ = Task.Run(async () =>
        {
            try
            {
                // Show the early result (a slow provider still working) right away, then the full one.
                var enriched = await _resolver.ResolveAsync(seed, ct,
                    ResolvePriority.Foreground, early => ShowNowEnriched(early, sig)).ConfigureAwait(false);
                ShowNowEnriched(enriched, sig);
            }
            catch (OperationCanceledException) { }
            catch (Exception ex) { Trace.WriteLine($"[hzn-queue-vm] enrich current: {ex.Message}"); }
        });
    }

    private void ShowNowEnriched(Track enriched, string sig)
    {
        var art = enriched.AlbumArt is { Length: > 0 } ? DecodeArt(enriched.AlbumArt) : null;
        Dispatcher.UIThread.Post(() =>
        {
            // Still the same track playing? (compare the signature, not the item —
            // a radio item stays referentially equal across songs).
            if (_queue?.Model.Current is not { } c || NowSig(c) != sig) return;
            // Sticky, so the next rebuild keeps it. The full result can lack art the early one
            // had only if a provider changed its mind, so keep earlier art rather than drop it.
            if (_nowEnrichedSig != sig) _nowEnrichedArt = null;
            _nowEnrichedSig = sig;
            _nowEnrichedTitle = enriched.Title;
            _nowEnrichedArtist = enriched.Artist;
            if (art != null) _nowEnrichedArt = art;

            if (!string.IsNullOrWhiteSpace(enriched.Title)) NowPlayingTitle = enriched.Title;
            NowPlayingSubtitle = enriched.Artist;
            if (_nowEnrichedArt != null) NowPlayingArt = _nowEnrichedArt;
        });
    }

    // Identifies a specific track within a queue item: id + everything the lookup searches
    // with. A radio station keeps one id but changes title/artist as songs roll over, and a
    // song can be republished with new interpretations (the title model refining a messy
    // stream title) without its primary title or artist changing.
    private static string NowSig(QueueItem item)
    {
        var m = item.Metadata;
        var sig = $"{item.Id}|{m.Title}|{m.Artist}|{m.ExternalId}";
        if (m.Candidates is { Count: > 0 } candidates)
            sig += "|" + string.Join("|", candidates.Select(c => $"{c.Artist}/{c.Title}"));
        return sig;
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

        for (var i = 0; i < Upcoming.Count; i++)
        {
            Upcoming[i].IsFirst = i == 0;
            Upcoming[i].IsLast = i == Upcoming.Count - 1;
        }
    }

    // The context peek is recomputed on every Changed (twice per track), but it
    // usually hasn't changed track-to-track. Skip the Clear+Add — which resets the
    // whole ItemsControl — when the rows are identical (QueuePreview is a record,
    // so this is structural equality).
    private void SyncContext(IReadOnlyList<QueuePreview> peek)
    {
        if (ContextUpcoming.Select(r => r.Preview).SequenceEqual(peek)) return;
        ContextUpcoming.Clear();
        foreach (var p in peek)
        {
            var song = p.Origin is { } origin ? Song.FromEntry(origin, p.Title, p.Subtitle) : null;
            ContextUpcoming.Add(new QueuePreviewRow(p, _songs?.For(song)));
        }
    }

    private QueueRowViewModel CreateRow(QueueItem item) => new(
        _songs?.For(Song.FromTrack(item.Metadata)),
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
            catch (Exception ex) { Trace.WriteLine($"[hzn-queue-vm] meta-ahead: {ex.Message}"); }

            // Background: the song playing now goes ahead of upcoming rows at every provider.
            var final = _resolver is { HasContributors: true }
                ? await _resolver.ResolveAsync(seed, CancellationToken.None, ResolvePriority.Background).ConfigureAwait(false)
                : seed;

            var art = final.AlbumArt is { Length: > 0 } ? DecodeArt(final.AlbumArt) : null;
            Dispatcher.UIThread.Post(() =>
            {
                if (!string.IsNullOrWhiteSpace(final.Title)) row.Title = final.Title;
                if (!string.IsNullOrWhiteSpace(final.Artist)) row.Subtitle = final.Artist;
                if (art != null) row.Thumbnail = art;
                if (row.Song is { } target)
                    row.Song = _songs?.For(target.Song with { Title = row.Title, Subtitle = row.Subtitle });
            });
        }
        catch (Exception ex) { Trace.WriteLine($"[hzn-queue-vm] enrich row: {ex.Message}"); }
        finally { MetaGate.Release(); }
    }

    private Bitmap? DecodeArtCached(byte[]? bytes)
    {
        if (bytes is null) return null;
        if (ReferenceEquals(_artCacheBytes, bytes) && _artCacheBmp != null) return _artCacheBmp;
        _artCacheBytes = bytes;
        _artCacheBmp = DecodeArt(bytes);
        return _artCacheBmp;
    }

    private static Bitmap? DecodeArt(byte[]? bytes) => Imaging.ImageBytes.ToBitmap(bytes);

    partial void OnHasNowPlayingChanged(bool value) => OnPropertyChanged(nameof(IsEmpty));
    partial void OnHasUpcomingChanged(bool value) => OnPropertyChanged(nameof(IsEmpty));
    partial void OnHasContextChanged(bool value) => OnPropertyChanged(nameof(IsEmpty));

    partial void OnIsSharedChanged(bool value)
    {
        OnPropertyChanged(nameof(Title));
        OnPropertyChanged(nameof(EmptyText));
    }

    /// <summary>Drag-and-drop reorder: move the dragged row to the dropped-on row's
    /// position. Called from the view's drop handler.</summary>
    public void ReorderTo(string sourceId, string targetId) =>
        _queue?.Model.MoveExplicitTo(sourceId, targetId);

    [RelayCommand]
    private async Task ClearQueueAsync()
    {
        if (_queue is null) return;
        if (!await _confirm.ConfirmAsync(new Confirmation(
                "Clear the queue?",
                "The songs waiting in the queue are removed. What's playing keeps playing.",
                "Clear Queue")))
            return;
        _queue.Model.ClearExplicit();
    }

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
            Trace.WriteLine($"[hzn-queue-vm] enqueue failed: {ex.Message}");
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

    /// <summary>The track, for the row's right-click menu.</summary>
    [ObservableProperty] private SongTarget? song;

    /// <summary>At the top or the bottom of the queue, where it can't move further.</summary>
    [ObservableProperty] private bool isFirst;
    [ObservableProperty] private bool isLast;

    public bool HasSubtitle => !string.IsNullOrWhiteSpace(Subtitle);
    public bool HasThumbnail => Thumbnail != null;

    public ICommand PlayNowCommand { get; }
    public ICommand RemoveCommand { get; }
    public ICommand MoveUpCommand { get; }
    public ICommand MoveDownCommand { get; }

    public QueueRowViewModel(
        SongTarget? song,
        string id, string title, string subtitle, Bitmap? thumbnail,
        Action<string> playNow, Action<string> remove,
        Action<string> moveUp, Action<string> moveDown)
    {
        this.song = song;
        Id = id;
        this.title = title;
        this.subtitle = subtitle;
        this.thumbnail = thumbnail;
        // Playing now interrupts what's playing, so a double-click on the art's play button
        // (two clicks) plays once.
        var lastPlay = DateTime.MinValue;
        PlayNowCommand = new RelayCommand(() =>
        {
            var now = DateTime.UtcNow;
            if (now - lastPlay < TimeSpan.FromMilliseconds(500)) return;
            lastPlay = now;
            playNow(id);
        });
        RemoveCommand = new RelayCommand(() => remove(id));
        MoveUpCommand = new RelayCommand(() => moveUp(id));
        MoveDownCommand = new RelayCommand(() => moveDown(id));
    }

    partial void OnSubtitleChanged(string value) => OnPropertyChanged(nameof(HasSubtitle));
    partial void OnThumbnailChanged(Bitmap? value) => OnPropertyChanged(nameof(HasThumbnail));
}

/// <summary>A "Next From" row: the preview, and what plays it, for its right-click menu.</summary>
public sealed record QueuePreviewRow(QueuePreview Preview, SongTarget? Song)
{
    public string Title => Preview.Title;
    public string Subtitle => Preview.Subtitle;
    public bool IsPlaceholder => Preview.IsPlaceholder;
}
