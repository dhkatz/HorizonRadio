using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Input;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using HorizonRadio.Core.History;
using HorizonRadio.Core.Metadata;
using HorizonRadio.Core.Sources.Queue;
using HorizonRadio.Sdk;
using HorizonRadio.Sdk.Metadata;
using HorizonRadio.Sdk.Models;
using HorizonRadio.Sdk.Sources;
using HorizonRadio.Source.Spotify;
using HorizonRadio.UI.Services;
using ShadUI;

namespace HorizonRadio.UI.ViewModels;

/// <summary>
/// The History tab: a newest-first list of every song the app played. Mirrors the
/// <see cref="PlayHistoryStore"/> into bound rows and lets the user play one again. Songs played
/// from a re-addressable source replay in one click; a radio song can once Core has identified it
/// and found it on the services (<see cref="HistoryIdentifier"/>), which also sets the "couldn't
/// identify" warning that opens a pre-filled GitHub issue. The rows' cover art is looked up here.
/// </summary>
public sealed partial class HistoryViewModel : ViewModelBase
{
    private readonly PlayHistoryStore? _store;
    private readonly QueuePlayback? _queue;
    private readonly MetadataResolver? _resolver;
    private readonly Action<string>? _openSearch;
    private readonly ToastManager? _toasts;
    private readonly SongActions? _songs;
    private readonly HistoryIdentifier? _identifier;
    private readonly IConfirmations _confirm = ProceedWithoutAsking.Instance;

    // Rows whose art has been looked up. A row is removed from here if the lookup failed, so it's
    // retried on the next rebuild / tab view rather than stuck forever.
    private readonly HashSet<string> _enriched = new();

    public ObservableCollection<HistoryRowViewModel> Items { get; } = new();

    [ObservableProperty] private bool isEmpty = true;

    public HistoryViewModel(
        PlayHistoryStore store,
        QueuePlayback queue,
        MetadataResolver? resolver,
        Action<string>? openSearch,
        ToastManager? toasts,
        SongActions? songs = null,
        IConfirmations? confirmations = null,
        HistoryIdentifier? identifier = null)
    {
        _identifier = identifier;
        _store = store;
        _confirm = confirmations ?? ProceedWithoutAsking.Instance;
        _queue = queue;
        _resolver = resolver;
        _openSearch = openSearch;
        _toasts = toasts;
        _songs = songs;
        _store.Changed += OnStoreChanged;
        Rebuild();
    }

    /// <summary>Designer ctor — inert.</summary>
    public HistoryViewModel() { }

    private void OnStoreChanged() => Dispatcher.UIThread.Post(Rebuild);

    private void Rebuild()
    {
        if (_store == null) return;
        var entries = _store.All; // newest first

        // In-place sync by id so a lazily-resolved source/verdict update (which fires Changed)
        // doesn't tear down and re-enrich every row.
        for (var i = Items.Count - 1; i >= 0; i--)
            if (!entries.Any(e => e.Id == Items[i].Id)) Items.RemoveAt(i);

        for (var i = 0; i < entries.Count; i++)
        {
            var entry = entries[i];
            if (i < Items.Count && Items[i].Id == entry.Id)
            {
                Items[i].SyncFromEntry(); // verdict / sources may have resolved
                continue;
            }
            var existing = Items.FirstOrDefault(r => r.Id == entry.Id);
            if (existing != null) Items.Move(Items.IndexOf(existing), i);
            else Items.Insert(i, CreateRow(entry));
        }

        IsEmpty = Items.Count == 0;
        EnrichRows();
    }

    /// <summary>Look again at rows that couldn't be settled. Called when the History tab is shown,
    /// so a song that couldn't be found earlier (a search source signed in since) gets another
    /// chance without waiting for a store change.</summary>
    public void RefreshEnrichment()
    {
        _identifier?.Retry();
        EnrichRows();
    }

    private HistoryRowViewModel CreateRow(PlayHistoryEntry entry) => new(
        entry,
        play: PlayAsync,
        report: e => GitHubReport.OpenIssueDraft(e, _toasts),
        remove: id => _store?.Remove(id),
        song: e => _songs?.For(SongOf(e)));

    private void EnrichRows()
    {
        // Every row gets its art (not just a fixed window); the shared gate keeps the lookups
        // trickling a few at a time, and they're cached, so the identifier's are shared.
        if (_resolver is not { HasContributors: true }) return;
        foreach (var row in Items)
            if (_enriched.Add(row.Id)) _ = EnrichRowAsync(row);
    }

    // Look up a row's cover art. Throttled, off the UI thread.
    private async Task EnrichRowAsync(HistoryRowViewModel row)
    {
        await EnrichmentThrottle.Gate.WaitAsync().ConfigureAwait(false);
        try
        {
            // Background: the song playing now goes ahead of History rows at every provider.
            var final = (await _resolver!.ResolveDetailedAsync(BuildSeed(row.Entry), CancellationToken.None,
                ResolvePriority.Background).ConfigureAwait(false)).Track;
            var art = final.AlbumArt is { Length: > 0 } ? DecodeArt(final.AlbumArt) : null;
            Dispatcher.UIThread.Post(() =>
            {
                if (art != null) row.Thumbnail = art;
                row.SyncFromEntry();
            });
        }
        catch (Exception ex)
        {
            Trace.WriteLine($"[hzn-history-vm] enrich: {ex.Message}");
            Dispatcher.UIThread.Post(() => _enriched.Remove(row.Id)); // let a later pass retry
        }
        finally { EnrichmentThrottle.Gate.Release(); }
    }

    // A played song as something to add to the queue or a mix: its first playable source, else
    // (a radio song nothing has been found for yet) its details, to find it when it's added.
    private static Song SongOf(PlayHistoryEntry e)
    {
        var title = string.IsNullOrWhiteSpace(e.Title) ? "Unknown track" : e.Title;
        var artist = string.IsNullOrWhiteSpace(e.Artist) ? null : e.Artist;
        var origin = e.Sources.Count > 0 ? new ContentRef(e.Sources[0].SourceId, e.Sources[0].Locator, title) : null;
        return new Song(title, artist, origin, BuildSeed(e));
    }

    // Reconstruct a resolver seed from a stored entry. A Spotify uri (kept as a replay source) also
    // serves as the metadata ExternalId so the Spotify/MusicBrainz providers can match by id;
    // everything else matches on text, with the freeform candidates carried for better matching.
    private static Track BuildSeed(PlayHistoryEntry e)
    {
        var spotify = e.Sources.FirstOrDefault(s => s.SourceId == SpotifyContentSourceFactory.SourceId);
        var candidates = e.Candidates.Count > 0
            ? e.Candidates.Select(c => new TitleCandidate(c.Artist, c.Title)).ToList()
            : null;
        return new Track(e.Title, e.Artist, e.Album, null, e.SourceId, e.SourceDisplay,
            ExternalId: spotify?.Locator, Year: e.Year, Candidates: candidates);
    }

    // Play a specific source, or (source == null) the preferred one. Falls back to a live search
    // only when there's nothing stored — e.g. a song no service had.
    private async Task PlayAsync(PlayHistoryEntry e, ReplaySource? source)
    {
        if (_queue == null) return;
        var target = source ?? (e.Sources.Count > 0 ? e.Sources[0] : null);
        if (target != null)
        {
            try
            {
                await _queue.EnqueueAsync(new ContentRef(target.SourceId, target.Locator, e.Title), playNow: true);
                return;
            }
            catch (Exception ex)
            {
                _toasts?.CreateToast("Couldn't play that")
                    .WithContent($"{ex.Message} Searching for it instead…")
                    .WithDelay(6).DismissOnClick().ShowWarning();
            }
        }

        _openSearch?.Invoke(BuildQuery(e));
    }

    private static string BuildQuery(PlayHistoryEntry e) =>
        string.IsNullOrWhiteSpace(e.Artist) ? e.Title : $"{e.Artist} {e.Title}";

    private static Bitmap? DecodeArt(byte[]? bytes) => Imaging.ImageBytes.ToBitmap(bytes);

    [RelayCommand]
    private async Task ClearAsync()
    {
        if (_store is null) return;
        if (!await _confirm.ConfirmAsync(new Confirmation(
                "Clear your history?",
                "Every song you've played is removed from History, on your other devices too if you've paired them. This can't be undone.",
                "Clear History")))
            return;
        _store.Clear();
    }
}

/// <summary>One play target offered by a row's Play picker (a service + its action).</summary>
public sealed record ReplaySourceOption(string Label, ICommand PlayCommand);

/// <summary>One row in the History list. Identity is immutable; <see cref="Thumbnail"/>,
/// <see cref="MatchState"/> and <see cref="PlaySources"/> are observable so background enrichment
/// (art, a resolved verdict, discovered playable sources) can update them in place.</summary>
public sealed partial class HistoryRowViewModel : ViewModelBase
{
    public PlayHistoryEntry Entry { get; }
    private readonly Func<PlayHistoryEntry, ReplaySource?, Task> _play;
    private readonly Func<PlayHistoryEntry, SongTarget?> _song;

    public string Id => Entry.Id;
    public string Title { get; }
    public string Subtitle { get; }
    public string SourceLabel { get; }
    public string PlayedAtLabel { get; }

    [ObservableProperty] private Bitmap? thumbnail;
    [ObservableProperty] private HistoryMatchState matchState;
    [ObservableProperty] private IReadOnlyList<ReplaySourceOption> playSources = [];

    /// <summary>The song, for its Add To Queue and Add To Mix buttons and right-click menu.</summary>
    [ObservableProperty] private SongTarget? song;

    public bool HasThumbnail => Thumbnail != null;
    public bool HasSubtitle => !string.IsNullOrWhiteSpace(Subtitle);

    /// <summary>The "couldn't identify this song" warning — only for an unconfirmed freeform song.</summary>
    public bool ShowWarning => MatchState == HistoryMatchState.Unmatched;

    /// <summary>True when the song is playable from more than one service (offer a play-from picker).</summary>
    public bool HasMultipleSources => PlaySources.Count > 1;

    public ICommand PlayCommand { get; }
    public ICommand ReportCommand { get; }
    public ICommand RemoveCommand { get; }

    public HistoryRowViewModel(
        PlayHistoryEntry entry,
        Func<PlayHistoryEntry, ReplaySource?, Task> play,
        Action<PlayHistoryEntry> report,
        Action<string> remove,
        Func<PlayHistoryEntry, SongTarget?> song)
    {
        Entry = entry;
        _play = play;
        _song = song;
        Title = string.IsNullOrWhiteSpace(entry.Title) ? "Unknown track" : entry.Title;
        Subtitle = entry.Artist;
        SourceLabel = entry.SourceDisplay;
        PlayedAtLabel = entry.PlayedAt.ToLocalTime().ToString("g", CultureInfo.CurrentCulture);
        matchState = entry.MatchState;

        PlayCommand = new AsyncRelayCommand(() => _play(entry, null));
        ReportCommand = new RelayCommand(() => report(entry));
        RemoveCommand = new RelayCommand(() => remove(entry.Id));
        SyncFromEntry();
    }

    /// <summary>Pull the latest verdict + sources off the (in-place-mutated) entry.</summary>
    public void SyncFromEntry()
    {
        MatchState = Entry.MatchState;
        PlaySources = Entry.Sources
            .Select(s => new ReplaySourceOption(s.SourceDisplay, new AsyncRelayCommand(() => _play(Entry, s))))
            .ToList();
        // Sources found since (a radio song identified) change what Add To Queue adds.
        Song = _song(Entry);
    }

    partial void OnThumbnailChanged(Bitmap? value) => OnPropertyChanged(nameof(HasThumbnail));
    partial void OnMatchStateChanged(HistoryMatchState value) => OnPropertyChanged(nameof(ShowWarning));
    partial void OnPlaySourcesChanged(IReadOnlyList<ReplaySourceOption> value) => OnPropertyChanged(nameof(HasMultipleSources));
}
