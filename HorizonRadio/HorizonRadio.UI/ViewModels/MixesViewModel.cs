using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Input;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using HorizonRadio.Core.Playback;
using HorizonRadio.Core.Sources;
using HorizonRadio.Core.Sources.Config;
using HorizonRadio.Core.Sources.Mixes;
using HorizonRadio.Core.Sources.Queue;
using HorizonRadio.Sdk;
using HorizonRadio.Sdk.Sources;
using Material.Icons;
using ShadUI;

namespace HorizonRadio.UI.ViewModels;

/// <summary>
/// Mixes tab: the library of cross-source playlists, plus an inline builder for creating or
/// editing one. Every entry is shown as what it is (a track's title, artist, and cover; a
/// playlist's name, owner, and size), looked up through <see cref="MixEntryDescriber"/> as soon as
/// it's added and kept across sessions, so a mix reads as music rather than a list of links.
/// Switching to a mix starts it on the runner via <see cref="MixSwitcher"/>.
/// </summary>
public sealed partial class MixesViewModel : ViewModelBase
{
    private readonly MixStore _store;
    private readonly MixSwitcher _switcher;
    private readonly DialogManager? _dialogs;
    private readonly MixEntryDescriber? _describer;
    private readonly SongActions? _songs;

    // One lookup per entry at a time, shared by every row showing it (the library card and the
    // editor). UI thread only.
    private readonly Dictionary<string, Task<MixEntryInfo>> _lookups = new();

    // Decoded covers, so a card and its editor rows don't each decode the same bytes.
    private readonly ConditionalWeakTable<byte[], Bitmap> _bitmaps = new();

    public ObservableCollection<MixCardViewModel> Mixes { get; } = new();

    /// <summary>Sources an entry can use — content-addressable only (a mix can't
    /// hold a self-driven source like Spotify Connect).</summary>
    public IReadOnlyList<IAudioSourceFactory> EntrySources { get; }

    public ObservableCollection<MixEntryRowViewModel> EditEntries { get; } = new();

    [ObservableProperty] private bool isEditing;
    [ObservableProperty] private string editingName = "";
    [ObservableProperty] private string statusMessage = "";
    [ObservableProperty] private bool hasError;

    /// <summary>The add bar's link/path box. Pasting a locator a source recognises picks that
    /// source (<see cref="NewSource"/>).</summary>
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(AddEntryCommand))]
    private string newLocator = "";

    [ObservableProperty] private IAudioSourceFactory? newSource;
    [ObservableProperty] private string addError = "";

    public bool HasMixes => Mixes.Count > 0;
    public bool HasEditEntries => EditEntries.Count > 0;
    public string EditorTitle => _editingId is null ? "New Mix" : "Edit Mix";
    public string NewLocatorHint => (NewSource as IContentSourceFactory)?.LocatorHint ?? "Paste a link, or a folder or file path";

    /// <summary>"5 entries · 120 tracks" under the editor's entry list (tracks once known).</summary>
    public string EditSummary => DescribeCount(EditEntries.Count, EditEntries.Select(e => e.ItemCount));

    // null = creating; otherwise the id being edited.
    private string? _editingId;

    // The mix as the editor opened it, to tell whether Cancel throws edits away.
    private string _openedName = "";
    private IReadOnlyList<ContentRef> _openedEntries = [];

    private readonly IConfirmations _confirm = ProceedWithoutAsking.Instance;
    private readonly SourceCatalog _sources = SourceCatalog.Empty;

    public MixesViewModel(MixStore store, MixSwitcher switcher, SourceCatalog sources,
                          DialogManager? dialogs = null, MixEntryDescriber? describer = null,
                          SongActions? songs = null, IConfirmations? confirmations = null)
    {
        _store = store;
        _confirm = confirmations ?? ProceedWithoutAsking.Instance;
        _switcher = switcher;
        _dialogs = dialogs;
        _describer = describer;
        _songs = songs;
        _sources = sources;
        EntrySources = sources.All.Where(f => f is IContentSourceFactory).ToList();
        newSource = EntrySources.Count > 0 ? EntrySources[0] : null;

        EditEntries.CollectionChanged += (_, _) => OnEditEntriesChanged();
        _store.Changed += OnStoreChanged;
        RefreshList();
    }

    /// <summary>Designer-only ctor.</summary>
    public MixesViewModel() : this(
        new MixStore(),
        DesignerSwitcher(),
        SourceCatalog.Empty)
    { }

    private static MixSwitcher DesignerSwitcher()
    {
        var runner = new SourceRunner(new NullSink());
        var config = new SourceConfigStore();
        var playback = new PlaybackRouter(runner, config, new MixStore(), new MixContentResolver(config, SourceCatalog.Empty));
        return new MixSwitcher(new MixStore(), playback.Player, runner);
    }

    private sealed class NullSink : IPcmSink
    {
        public bool Send(ReadOnlySpan<short> samples) => false;
    }

    private void OnStoreChanged() => Dispatcher.UIThread.Post(RefreshList);

    // ── Library ──────────────────────────────────────────────────────────────

    // How many entries a card shows: covers for its mosaic, titles for its summary line.
    private const int CardPreviewEntries = 4;

    private void RefreshList()
    {
        Mixes.Clear();
        foreach (var m in _store.All) Mixes.Add(ToCard(m));
        OnPropertyChanged(nameof(HasMixes));
    }

    private MixCardViewModel ToCard(Mix m)
    {
        var id = m.Id;
        var card = new MixCardViewModel(id, m.Name, m.Entries.Count)
        {
            PlayCommand = new RelayCommand(() => PlayMix(id)),
            EditCommand = new RelayCommand(() => EditMix(id)),
            DeleteCommand = new AsyncRelayCommand(() => DeleteMixAsync(id)),
        };

        var preview = m.Entries.Take(CardPreviewEntries).ToList();
        var infos = new MixEntryInfo?[preview.Count];
        void Show()
        {
            card.Summary = string.Join(" · ", preview.Select((e, i) => infos[i]?.Title ?? Fallback(e)))
                           + (m.Entries.Count > preview.Count ? " · …" : "");
            // Distinct covers only: four tracks off one album make one cover, not a mosaic of it.
            card.SetCovers(infos
                .Select(i => i?.Art)
                .OfType<byte[]>()
                .Where(a => a.Length > 0)
                .DistinctBy(a => Convert.ToBase64String(SHA256.HashData(a)))
                .Select(Decode)
                .OfType<Bitmap>()
                .ToList());
        }
        for (var i = 0; i < preview.Count; i++)
        {
            var index = i;
            if (_describer?.Cached(preview[i]) is { } cached) infos[i] = cached.Info;
            else _ = WhenDescribed(preview[i], info => { infos[index] = info; Show(); });
        }
        Show();
        return card;
    }

    // What an entry is called until it's looked up: its saved name, else its locator.
    private static string Fallback(ContentRef e) => e.DisplayName ?? e.Locator;

    // ── Builder ──────────────────────────────────────────────────────────────

    [RelayCommand]
    private void New()
    {
        _editingId = null;
        OpenEditor("", []);
    }

    private void EditMix(string id)
    {
        var mix = _store.Get(id);
        if (mix is null) return;
        _editingId = mix.Id;
        OpenEditor(mix.Name, mix.Entries);
    }

    private void OpenEditor(string name, IReadOnlyList<ContentRef> entries)
    {
        EditingName = name;
        _openedName = name;
        _openedEntries = entries;
        StatusMessage = "";
        HasError = false;
        NewLocator = "";
        AddError = "";
        EditEntries.Clear();
        foreach (var e in entries) EditEntries.Add(NewEntryRow(e));
        OnPropertyChanged(nameof(EditorTitle));
        IsEditing = true;
    }

    [RelayCommand]
    private async Task CancelAsync()
    {
        var edited = EditingName != _openedName
                     || !EditEntries.Select(e => e.Entry).SequenceEqual(_openedEntries);
        if (edited && !await _confirm.ConfirmAsync(new Confirmation(
                "Discard your changes?",
                "Your changes to this mix haven't been saved.",
                "Discard")))
            return;
        IsEditing = false;
        EditEntries.Clear();
    }

    /// <summary>Start a new mix with this entry in it (New Mix… on a song's menu).</summary>
    public void NewMixWith(ContentRef entry, MixEntryInfo? known)
    {
        if (known != null) _describer?.Remember(entry, known);
        _editingId = null;
        OpenEditor("", [entry]);
    }

    /// <summary>Add an entry to the end of a saved mix (Add To Mix on a song's menu). When that mix
    /// is open in the editor, the entry shows there too, so saving the editor keeps it.</summary>
    public MixAddResult AddToMix(string mixId, ContentRef entry, MixEntryInfo? known)
    {
        if (_store.Get(mixId) is not { } mix) return MixAddResult.NoSuchMix;
        var editing = IsEditing && _editingId == mixId;
        if (mix.Entries.Any(e => SameEntry(e, entry)) || (editing && EditEntries.Any(r => SameEntry(r.Entry, entry))))
            return MixAddResult.AlreadyInMix;

        if (known != null) _describer?.Remember(entry, known);
        _store.AddOrUpdate(mix with { Entries = [.. mix.Entries, entry] });
        _store.SaveToDisk();
        if (editing) EditEntries.Add(NewEntryRow(entry));
        return MixAddResult.Added;
    }

    private static bool SameEntry(ContentRef a, ContentRef b) => a.SourceId == b.SourceId && a.Locator == b.Locator;

    private bool CanAddEntry => !string.IsNullOrWhiteSpace(NewLocator);

    /// <summary>Add what's in the add bar as an entry. It shows at once and fills in its title
    /// and cover as the lookup answers.</summary>
    [RelayCommand(CanExecute = nameof(CanAddEntry))]
    private void AddEntry()
    {
        var locator = CleanLocator(NewLocator);
        if (locator.Length == 0) return;
        var source = EntrySources.FirstOrDefault(f => Recognizes(f, locator)) ?? NewSource;
        if (source is null) { AddError = "Pick a source for this entry."; return; }

        var entry = new ContentRef(source.Id, locator);
        if (EditEntries.Any(r => SameEntry(r.Entry, entry)))
        {
            AddError = "That's already in this mix.";
            return;
        }

        EditEntries.Add(NewEntryRow(entry));
        NewLocator = "";
        AddError = "";
    }

    // Pasted paths often come quoted ("Copy as path"), and links with stray whitespace.
    private static string CleanLocator(string s) => s.Trim().Trim('"').Trim();

    private static bool Recognizes(IAudioSourceFactory f, string locator) =>
        f is IContentSourceFactory c && c.Recognizes(locator);

    partial void OnNewLocatorChanged(string value)
    {
        AddError = "";
        var locator = CleanLocator(value);
        if (EntrySources.FirstOrDefault(f => Recognizes(f, locator)) is { } source) NewSource = source;
    }

    partial void OnNewSourceChanged(IAudioSourceFactory? value) => OnPropertyChanged(nameof(NewLocatorHint));

    // Rows carry their own remove/reorder commands (closing over these helpers) so
    // the item template binds straight to the row instead of reaching up the tree.
    private MixEntryRowViewModel NewEntryRow(ContentRef entry)
    {
        var source = _sources.Find(entry.SourceId);
        var row = new MixEntryRowViewModel(entry, source?.DisplayName ?? entry.SourceId,
            RemoveEntryRow, MoveEntryRow, r => LoadRow(r, force: true));
        row.Song = _songs?.For(Song.FromEntry(entry, row.Title));
        LoadRow(row, force: false);
        return row;
    }

    private void RemoveEntryRow(MixEntryRowViewModel row) => EditEntries.Remove(row);

    private void MoveEntryRow(MixEntryRowViewModel row, int delta)
    {
        var i = EditEntries.IndexOf(row);
        var j = i + delta;
        if (i < 0 || j < 0 || j >= EditEntries.Count) return;
        EditEntries.Move(i, j);
    }

    private void OnEditEntriesChanged()
    {
        for (var i = 0; i < EditEntries.Count; i++)
        {
            var row = EditEntries[i];
            row.Position = i + 1;
            row.IsFirst = i == 0;
            row.IsLast = i == EditEntries.Count - 1;
        }
        OnPropertyChanged(nameof(HasEditEntries));
        OnPropertyChanged(nameof(EditSummary));
    }

    // Show what's kept for the entry at once; look it up when there's nothing kept, it's old,
    // or the user asked (Retry).
    private void LoadRow(MixEntryRowViewModel row, bool force)
    {
        var cached = force ? null : _describer?.Cached(row.Entry);
        if (cached is { } c) Show(row, c.Info);
        if (_describer is null || cached is { Stale: false }) return;

        row.Error = null;
        row.IsLoading = cached is null;
        _ = WhenDescribed(row.Entry,
            info =>
            {
                row.IsLoading = false;
                Show(row, info);
            },
            ex =>
            {
                row.IsLoading = false;
                // A failed refresh keeps what was shown; only an entry with nothing says so.
                if (cached is null) row.Error = ErrorText(ex);
            });
    }

    private void Show(MixEntryRowViewModel row, MixEntryInfo info)
    {
        row.Apply(info, Decode(info.Art));
        row.Song = _songs?.For(Song.FromEntry(row.Entry, info.Title, info.Subtitle, info));
        OnPropertyChanged(nameof(EditSummary));
    }

    private static string ErrorText(Exception? ex) => ex switch
    {
        InvalidOperationException { Message.Length: > 0 } ioe => ioe.Message,
        _ => "Couldn't look this up. Check the link, then retry.",
    };

    // Look an entry up and hand the answer (or the failure) back on the UI thread.
    private async Task WhenDescribed(ContentRef entry, Action<MixEntryInfo> onInfo, Action<Exception>? onError = null)
    {
        if (_describer is null) return;
        try
        {
            var info = await LookUpAsync(entry, _describer);
            Dispatcher.UIThread.Post(() => onInfo(info));
        }
        catch (Exception ex)
        {
            if (onError != null) Dispatcher.UIThread.Post(() => onError(ex));
        }
    }

    // One lookup per entry at a time; the next ask after it settles looks it up again.
    private Task<MixEntryInfo> LookUpAsync(ContentRef entry, MixEntryDescriber describer)
    {
        var key = $"{entry.SourceId}\n{entry.Locator}";
        // A settled lookup whose removal is still queued doesn't count: a Retry must look again.
        if (_lookups.TryGetValue(key, out var running) && !running.IsCompleted) return running;

        var task = Task.Run(async () =>
        {
            // Shares the queue's and History's cap, so opening a big mix can't start a yt-dlp
            // process per entry all at once.
            await EnrichmentThrottle.Gate.WaitAsync().ConfigureAwait(false);
            try { return await describer.DescribeAsync(entry, CancellationToken.None).ConfigureAwait(false); }
            catch (Exception ex)
            {
                Trace.WriteLine($"[hzn-mixes-vm] describe {entry.SourceId} {entry.Locator}: {ex.Message}");
                throw;
            }
            finally { EnrichmentThrottle.Gate.Release(); }
        });
        _lookups[key] = task;
        _ = task.ContinueWith(_ => Dispatcher.UIThread.Post(() =>
        {
            if (_lookups.TryGetValue(key, out var current) && current == task) _lookups.Remove(key);
        }), TaskScheduler.Default);
        return task;
    }

    private Bitmap? Decode(byte[]? art)
    {
        if (art is not { Length: > 0 }) return null;
        if (_bitmaps.TryGetValue(art, out var bmp)) return bmp;
        bmp = Imaging.ImageBytes.ToBitmap(art);
        if (bmp != null) _bitmaps.AddOrUpdate(art, bmp);
        return bmp;
    }

    [RelayCommand]
    private void Save()
    {
        if (string.IsNullOrWhiteSpace(EditingName)) { Fail("Give the mix a name."); return; }
        if (EditEntries.Count == 0) { Fail("Add at least one entry: paste a link, or a folder or file path."); return; }

        // Keep each entry's title in mixes.json: the file stays readable, and an entry whose
        // lookup is gone still has a name.
        var entries = EditEntries
            .Select(r => r.Entry with { DisplayName = r.HasInfo ? r.Title : r.Entry.DisplayName })
            .ToList();

        var id = _editingId ?? Guid.NewGuid().ToString("n");
        _store.AddOrUpdate(new Mix(id, EditingName.Trim(), entries));
        _store.SaveToDisk();
        _describer?.KeepOnly(_store.All.SelectMany(m => m.Entries));

        IsEditing = false;
        EditEntries.Clear();
        StatusMessage = "";
        HasError = false;
    }

    private async Task DeleteMixAsync(string id)
    {
        var name = _store.Get(id)?.Name ?? "this mix";
        if (!await _confirm.ConfirmAsync(new Confirmation(
                $"Delete “{name}”?",
                "The mix is deleted, on your other devices too if you've paired them. The songs in it aren't affected.",
                "Delete Mix")))
            return;
        _store.Remove(id);
        _store.SaveToDisk();
        _describer?.KeepOnly(_store.All.SelectMany(m => m.Entries));
    }

    // Starting a mix while the queue already has content asks whether to replace the
    // queue's context or add this mix's tracks to it; otherwise it just plays.
    private void PlayMix(string id)
    {
        var mix = _store.Get(id);
        if (mix is null) return;

        if (_switcher.QueueHasContent && _dialogs != null)
        {
            var dialog = new QueueAddModeDialogViewModel(_dialogs, mix.Name);
            _dialogs.CreateDialog(dialog)
                .Dismissible()
                .WithSuccessCallback(vm => _ = PlayAsync(id, mix.Name, vm.Mode))
                .Show();
        }
        else
        {
            _ = PlayAsync(id, mix.Name, QueueAddMode.Replace);
        }
    }

    private async Task PlayAsync(string id, string name, QueueAddMode mode)
    {
        HasError = false;
        StatusMessage = mode == QueueAddMode.Add ? $"Adding {name} to the queue…" : $"Starting {name}…";
        try
        {
            if (mode == QueueAddMode.Add) await _switcher.AddToQueueAsync(id);
            else await _switcher.SwitchToAsync(id);
            StatusMessage = mode == QueueAddMode.Add ? $"Added to queue: {name}" : $"Playing: {name}";
        }
        catch (Exception ex)
        {
            Fail(ex.Message);
            Trace.WriteLine($"[hzn-mixes-vm] play failed: {ex}");
        }
    }

    private void Fail(string message)
    {
        HasError = true;
        StatusMessage = message;
    }

    /// <summary>"3 entries", or "3 entries · 41 tracks" once every entry's size is known.</summary>
    internal static string DescribeCount(int entries, IEnumerable<int?> itemCounts)
    {
        var text = $"{entries} entr{(entries == 1 ? "y" : "ies")}";
        var counts = itemCounts.ToList();
        if (entries == 0 || counts.Any(c => c is null)) return text;
        var tracks = counts.Sum(c => c!.Value);
        return $"{text} · {tracks} track{(tracks == 1 ? "" : "s")}";
    }
}

/// <summary>A mix in the library: its name, size, the first entries' titles, and a cover
/// mosaic from their art.</summary>
public sealed partial class MixCardViewModel : ViewModelBase
{
    public string Id { get; }
    public string Name { get; }
    public string CountText { get; }

    [ObservableProperty] private string summary = "";

    /// <summary>The single cover shown when there aren't four for a mosaic.</summary>
    [ObservableProperty] private Bitmap? cover;

    /// <summary>Four covers for a 2×2 mosaic, or empty.</summary>
    [ObservableProperty] private IReadOnlyList<Bitmap> mosaic = [];

    public bool HasCover => Cover != null && Mosaic.Count == 0;
    public bool HasMosaic => Mosaic.Count > 0;

    public required ICommand PlayCommand { get; init; }
    public required ICommand EditCommand { get; init; }
    public required ICommand DeleteCommand { get; init; }

    public MixCardViewModel(string id, string name, int entryCount)
    {
        Id = id;
        Name = name;
        CountText = entryCount == 0 ? "Empty" : $"{entryCount} entr{(entryCount == 1 ? "y" : "ies")}";
    }

    internal void SetCovers(IReadOnlyList<Bitmap> covers)
    {
        // Distinct covers only: four tracks off one album make a single cover, not a mosaic of it.
        var distinct = covers.Distinct().ToList();
        Mosaic = distinct.Count >= 4 ? distinct.Take(4).ToList() : [];
        Cover = distinct.FirstOrDefault();
    }

    partial void OnCoverChanged(Bitmap? value) => OnPropertyChanged(nameof(HasCover));

    partial void OnMosaicChanged(IReadOnlyList<Bitmap> value)
    {
        OnPropertyChanged(nameof(HasCover));
        OnPropertyChanged(nameof(HasMosaic));
    }
}

/// <summary>An entry in the mix builder: what it is (filled in as its lookup answers), where it
/// comes from, and its own remove/reorder/retry commands.</summary>
public sealed partial class MixEntryRowViewModel : ViewModelBase
{
    public ContentRef Entry { get; }
    public string SourceName { get; }

    /// <summary>The raw link or path, for the tooltip and while the entry isn't looked up.</summary>
    public string Locator => Entry.Locator;

    [ObservableProperty] private string title;
    [ObservableProperty] private string? subtitle;
    [ObservableProperty] private Bitmap? thumbnail;
    [ObservableProperty] private ContentKind? kind;
    [ObservableProperty] private int? itemCount;
    [ObservableProperty] private bool isLoading;
    [ObservableProperty] private string? error;
    [ObservableProperty] private bool hasInfo;
    [ObservableProperty] private int position;
    [ObservableProperty] private bool isFirst;
    [ObservableProperty] private bool isLast;

    /// <summary>The entry as a song, for its Add To Queue button and right-click menu.</summary>
    [ObservableProperty] private SongTarget? song;

    public bool HasThumbnail => Thumbnail != null;
    public bool HasError => Error != null;

    /// <summary>"Playlist · 24 tracks · Spotify", "Track · YouTube": the line under the subtitle.</summary>
    public string Details
    {
        get
        {
            var parts = new List<string>();
            if (Kind is { } k) parts.Add(KindName(k));
            if (ItemCount is { } n && Kind is not (ContentKind.Track or ContentKind.Station))
                parts.Add($"{n} track{(n == 1 ? "" : "s")}");
            parts.Add(SourceName);
            return string.Join(" · ", parts);
        }
    }

    /// <summary>The placeholder glyph behind the cover, by what the entry is.</summary>
    public MaterialIconKind Icon => Kind switch
    {
        ContentKind.Playlist => MaterialIconKind.PlaylistMusic,
        ContentKind.Album => MaterialIconKind.Album,
        ContentKind.Folder => MaterialIconKind.FolderMusic,
        ContentKind.Station => MaterialIconKind.Radio,
        _ => MaterialIconKind.MusicNote,
    };

    public ICommand RemoveCommand { get; }
    public ICommand MoveUpCommand { get; }
    public ICommand MoveDownCommand { get; }
    public ICommand RetryCommand { get; }

    public MixEntryRowViewModel(
        ContentRef entry,
        string sourceName,
        Action<MixEntryRowViewModel> remove,
        Action<MixEntryRowViewModel, int> move,
        Action<MixEntryRowViewModel> retry)
    {
        Entry = entry;
        SourceName = sourceName;
        title = entry.DisplayName ?? entry.Locator;
        RemoveCommand = new RelayCommand(() => remove(this));
        MoveUpCommand = new RelayCommand(() => move(this, -1));
        MoveDownCommand = new RelayCommand(() => move(this, +1));
        RetryCommand = new RelayCommand(() => retry(this));
    }

    internal void Apply(MixEntryInfo info, Bitmap? art)
    {
        Title = info.Title;
        Subtitle = info.Subtitle;
        Kind = info.Kind;
        ItemCount = info.ItemCount;
        Thumbnail = art;
        Error = null;
        HasInfo = true;
    }

    private static string KindName(ContentKind kind) => kind switch
    {
        ContentKind.Playlist => "Playlist",
        ContentKind.Album => "Album",
        ContentKind.Folder => "Folder",
        ContentKind.Station => "Radio station",
        _ => "Track",
    };

    partial void OnThumbnailChanged(Bitmap? value) => OnPropertyChanged(nameof(HasThumbnail));
    partial void OnErrorChanged(string? value) => OnPropertyChanged(nameof(HasError));
    partial void OnKindChanged(ContentKind? value)
    {
        OnPropertyChanged(nameof(Details));
        OnPropertyChanged(nameof(Icon));
    }
    partial void OnItemCountChanged(int? value) => OnPropertyChanged(nameof(Details));
}
