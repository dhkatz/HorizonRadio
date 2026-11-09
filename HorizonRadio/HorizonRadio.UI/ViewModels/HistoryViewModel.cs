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
using HorizonRadio.Core.Models;
using HorizonRadio.Core.Sources;
using HorizonRadio.Core.Sources.Queue;
using HorizonRadio.Core.Sources.Spotify;
using HorizonRadio.UI.Services;
using ShadUI;

namespace HorizonRadio.UI.ViewModels;

/// <summary>
/// The History tab: a newest-first list of every song the app played. Mirrors the
/// <see cref="PlayHistoryStore"/> into bound rows and lets the user play one again. Songs played
/// from a re-addressable source replay in one click; a freeform (radio) song is resolved lazily —
/// the row enrichment searches the services for the catalog-canonical name and stores a playable
/// URL per service, so replay becomes a one-click (or pick-a-source) action instead of a fuzzy
/// live search. The same enrichment refreshes the "couldn't identify" warning that opens a
/// pre-filled GitHub issue.
/// </summary>
public sealed partial class HistoryViewModel : ViewModelBase
{
    private readonly PlayHistoryStore? _store;
    private readonly QueuePlayback? _queue;
    private readonly MetadataResolver? _resolver;
    private readonly Action<string>? _openSearch;
    private readonly ToastManager? _toasts;
    private readonly SongActions? _songs;

    // Rows whose enrichment has settled (art + verdict + any playable sources resolved). A row is
    // removed from here if its enrichment couldn't complete (no search source ready yet, or a
    // transient error), so it's retried on the next rebuild / tab view rather than stuck forever.
    private readonly HashSet<string> _enriched = new();

    public ObservableCollection<HistoryRowViewModel> Items { get; } = new();

    [ObservableProperty] private bool isEmpty = true;

    public HistoryViewModel(
        PlayHistoryStore store,
        QueuePlayback queue,
        MetadataResolver? resolver,
        Action<string>? openSearch,
        ToastManager? toasts,
        SongActions? songs = null)
    {
        _store = store;
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

    /// <summary>Re-run enrichment for any not-yet-settled rows. Called when the History tab is
    /// shown, so rows that couldn't resolve earlier (e.g. a search source connected after startup)
    /// get another chance without waiting for a store change.</summary>
    public void RefreshEnrichment() => EnrichRows();

    private HistoryRowViewModel CreateRow(PlayHistoryEntry entry) => new(
        entry,
        play: PlayAsync,
        report: e => GitHubReport.OpenIssueDraft(e, _toasts),
        remove: id => _store?.Remove(id),
        song: e => _songs?.For(SongOf(e)));

    private void EnrichRows()
    {
        // Enrichment fetches art (needs the resolver) and, for freeform songs, playable sources
        // (needs a searchable source). Run if either is available. Every row is enriched (not just
        // a fixed window) so songs deep in the list still get art, a verdict, and replay sources;
        // the shared gate keeps the actual work trickling a few at a time.
        if (_resolver is not { HasContributors: true } && !UnifiedSearch.HasReadySource) return;
        foreach (var row in Items)
            if (_enriched.Add(row.Id)) _ = EnrichRowAsync(row);
    }

    // Resolve a row's art and — for a freeform song with no playable source yet — find one per
    // service by searching the catalog-canonical name, then store both. Throttled, off the UI thread.
    private async Task EnrichRowAsync(HistoryRowViewModel row)
    {
        await EnrichmentThrottle.Gate.WaitAsync().ConfigureAwait(false);
        var settled = true;
        try
        {
            var entry = row.Entry;
            var seed = BuildSeed(entry);
            var resolved = _resolver is { HasContributors: true }
                // Background: the song playing now goes ahead of History rows at every provider.
                ? await _resolver.ResolveDetailedAsync(seed, CancellationToken.None, ResolvePriority.Background).ConfigureAwait(false)
                : new MetadataResult(seed, false, []);
            var final = resolved.Track;
            var art = final.AlbumArt is { Length: > 0 } ? DecodeArt(final.AlbumArt) : null;

            // A freeform song (no re-addressable origin) needs identification + a playable URL.
            IReadOnlyList<ReplaySource>? foundSources = null;
            HistoryMatchState? newState = null;
            if (entry.Sources.Count == 0 && _resolver is { HasContributors: true })
            {
                if (resolved.Matched)
                {
                    // Catalog confirmed the identity. Prefer VocaDB's PV links (the exact official
                    // uploads, incl. Niconico) and supplement with a name search for any other
                    // service (e.g. Spotify). Searching only when matched is what keeps a fuzzy
                    // title-only parse from latching onto the wrong artist's same-titled track.
                    var pvSources = HistorySourceMatch.FromPvs(resolved.Playables);
                    var (searchHits, ran) = await SongSources.SearchAsync(final, CancellationToken.None).ConfigureAwait(false);
                    foundSources = HistorySourceMatch.Combine(pvSources, searchHits);
                    newState = HistoryMatchState.Matched;
                    // Settle when the search actually ran, or when we have PVs and there's no search
                    // source that could ever add more. If a search source exists but wasn't ready yet
                    // (ran == false), stay unsettled so a later pass can still add e.g. Spotify —
                    // having a PV mustn't permanently suppress the other services.
                    settled = ran || (pvSources.Count > 0 && !UnifiedSearch.HasSearchableSource);
                }
                else
                {
                    // Couldn't confirm what this song is → flag it for the report action.
                    newState = HistoryMatchState.Unmatched;
                }
            }

            Dispatcher.UIThread.Post(() =>
            {
                if (art != null) row.Thumbnail = art;
                if (foundSources != null) _store?.SetSources(entry.Id, foundSources);
                if (newState is { } ns) _store?.SetMatchState(entry.Id, ns);
                row.SyncFromEntry();
                if (!settled) _enriched.Remove(entry.Id); // retry on the next rebuild / tab view
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
        if (target != null && SourceCatalog.Find(target.SourceId) is IContentSourceFactory factory)
        {
            try
            {
                await _queue.EnqueueLocatorAsync(factory, target.Locator, playNow: true);
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
    private void Clear() => _store?.Clear();
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
