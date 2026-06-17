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
    // Per-source result ceiling for the playable-URL search (Spotify dev apps cap at 10).
    private const int SearchLimit = 10;

    private readonly PlayHistoryStore? _store;
    private readonly QueuePlayback? _queue;
    private readonly MetadataResolver? _resolver;
    private readonly Action<string>? _openSearch;
    private readonly ToastManager? _toasts;

    // Each row enriched at most once per view session; the resolver/search caches make it cheap.
    private readonly HashSet<string> _enriched = new();
    private const int EnrichLookahead = 30;

    // Cap concurrent resolves/searches so a long list trickles in rather than stampeding yt-dlp.
    private static readonly SemaphoreSlim MetaGate = new(3, 3);

    public ObservableCollection<HistoryRowViewModel> Items { get; } = new();

    [ObservableProperty] private bool isEmpty = true;

    public HistoryViewModel(
        PlayHistoryStore store,
        QueuePlayback queue,
        MetadataResolver? resolver,
        Action<string>? openSearch,
        ToastManager? toasts)
    {
        _store = store;
        _queue = queue;
        _resolver = resolver;
        _openSearch = openSearch;
        _toasts = toasts;
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
        EnrichWindow();
    }

    private HistoryRowViewModel CreateRow(PlayHistoryEntry entry) => new(
        entry,
        play: PlayAsync,
        report: e => GitHubReport.OpenIssueDraft(e, _toasts),
        remove: id => _store?.Remove(id));

    private void EnrichWindow()
    {
        // Enrichment fetches art (needs the resolver) and, for freeform songs, playable sources
        // (needs a searchable source). Run if either is available.
        if (_resolver is not { HasContributors: true } && !UnifiedSearch.HasReadySource) return;
        var n = Math.Min(EnrichLookahead, Items.Count);
        for (var i = 0; i < n; i++)
        {
            var row = Items[i];
            if (_enriched.Add(row.Id)) _ = EnrichRowAsync(row);
        }
    }

    // Resolve a row's art and — for a freeform song with no playable source yet — find one per
    // service by searching the catalog-canonical name, then store both. Throttled, off the UI thread.
    private async Task EnrichRowAsync(HistoryRowViewModel row)
    {
        await MetaGate.WaitAsync().ConfigureAwait(false);
        try
        {
            var entry = row.Entry;
            var seed = BuildSeed(entry);
            var (final, matched) = _resolver is { HasContributors: true }
                ? await _resolver.ResolveDetailedAsync(seed, CancellationToken.None).ConfigureAwait(false)
                : (seed, false);
            var art = final.AlbumArt is { Length: > 0 } ? DecodeArt(final.AlbumArt) : null;

            // Only freeform songs (no re-addressable origin) need a source lookup.
            IReadOnlyList<ReplaySource>? foundSources = null;
            HistoryMatchState? newState = null;
            if (entry.Sources.Count == 0)
            {
                foundSources = await FindSourcesAsync(final, CancellationToken.None).ConfigureAwait(false);
                // Identified if we found somewhere to play it, or a catalog confirmed it; else flag it.
                newState = foundSources.Count > 0 || matched ? HistoryMatchState.Matched : HistoryMatchState.Unmatched;
            }

            Dispatcher.UIThread.Post(() =>
            {
                if (art != null) row.Thumbnail = art;
                if (foundSources != null)
                {
                    _store?.SetSources(entry.Id, foundSources);
                    if (newState is { } ns) _store?.SetMatchState(entry.Id, ns);
                    row.SyncFromEntry();
                }
            });
        }
        catch (Exception ex) { Debug.WriteLine($"[hzn-history-vm] enrich: {ex.Message}"); }
        finally { MetaGate.Release(); }
    }

    // Find a playable URL per service for an identified song. Searches the canonical name and keeps
    // only the conservatively-matching hit per source (the multi-source set the picker offers).
    private static async Task<IReadOnlyList<ReplaySource>> FindSourcesAsync(Track song, CancellationToken ct)
    {
        if (!UnifiedSearch.HasReadySource) return [];
        var query = $"{song.Artist} {song.Title}".Trim();
        if (string.IsNullOrWhiteSpace(query)) return [];

        UnifiedSearchResult result;
        try { result = await UnifiedSearch.SearchAsync(query, SearchLimit, ct: ct).ConfigureAwait(false); }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex) { Debug.WriteLine($"[hzn-history-vm] source search: {ex.Message}"); return []; }

        return HistorySourceMatch.Select(song.Artist, song.Title, result.Results)
            .Select(r => new ReplaySource(r.SourceId, SourceCatalog.Find(r.SourceId)?.DisplayName ?? r.SourceId, r.Locator))
            .ToList();
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

    public string Id => Entry.Id;
    public string Title { get; }
    public string Subtitle { get; }
    public string SourceLabel { get; }
    public string PlayedAtLabel { get; }

    [ObservableProperty] private Bitmap? thumbnail;
    [ObservableProperty] private HistoryMatchState matchState;
    [ObservableProperty] private IReadOnlyList<ReplaySourceOption> playSources = [];

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
        Action<string> remove)
    {
        Entry = entry;
        _play = play;
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
    }

    partial void OnThumbnailChanged(Bitmap? value) => OnPropertyChanged(nameof(HasThumbnail));
    partial void OnMatchStateChanged(HistoryMatchState value) => OnPropertyChanged(nameof(ShowWarning));
    partial void OnPlaySourcesChanged(IReadOnlyList<ReplaySourceOption> value) => OnPropertyChanged(nameof(HasMultipleSources));
}
