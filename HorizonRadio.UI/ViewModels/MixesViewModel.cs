using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Linq;
using System.Threading.Tasks;
using System.Windows.Input;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using HorizonRadio.Core.Sources;
using HorizonRadio.Core.Sources.Config;
using HorizonRadio.Core.Sources.Mixes;
using HorizonRadio.Core.Sources.Queue;
using ShadUI;

namespace HorizonRadio.UI.ViewModels;

/// <summary>
/// Mixes tab: the library of cross-source playlists, plus an inline builder for
/// creating/editing one (name, an optional station override, and an ordered list
/// of entries — each a source + a locator). Switching to a mix starts it on the
/// runner via <see cref="MixSwitcher"/>.
/// </summary>
public sealed partial class MixesViewModel : ViewModelBase
{
    /// <summary>Station-override sentinel meaning "inherit the global target".</summary>
    public const string UseGlobalDefault = "Use global default";

    private readonly MixStore _store;
    private readonly MixSwitcher _switcher;
    private readonly DialogManager? _dialogs;
    private readonly MixContentResolver? _content;

    // Resolved "source: title" per entry (keyed sourceId|locator), so the list shows
    // a real title instead of a raw URL. Cached so editing a mix doesn't re-resolve.
    private readonly Dictionary<string, string> _entryTitles = new();

    // Cap concurrent entry resolves so opening the tab with many YouTube-first mixes
    // doesn't spawn one yt-dlp process per mix all at once — ≤3 enumerate at a time
    // on the Mixes tab (the queue's metadata-ahead has its own separate ≤3 cap).
    private static readonly System.Threading.SemaphoreSlim ResolveGate = new(3, 3);

    public ObservableCollection<MixRow> Mixes { get; } = new();

    /// <summary>Sources an entry can use — content-addressable only (a mix can't
    /// hold a self-driven source like Spotify Connect).</summary>
    public IReadOnlyList<IAudioSourceFactory> EntrySources { get; }

    /// <summary>Station-override choices: "use global default" (inherit) + the
    /// specific FH6 stations. "Any station" is deliberately omitted — it's the
    /// global default's job; a per-mix override pins the mix to one station.</summary>
    public ObservableCollection<string> StationOptions { get; }

    public ObservableCollection<MixEntryEditRow> EditEntries { get; } = new();

    [ObservableProperty] private bool isEditing;
    [ObservableProperty] private string editingName = "";
    [ObservableProperty] private string editingStation = UseGlobalDefault;
    [ObservableProperty] private string statusMessage = "";
    [ObservableProperty] private bool hasError;
    public bool HasMixes => Mixes.Count > 0;

    // null = creating; otherwise the id being edited.
    private string? _editingId;

    public MixesViewModel(MixStore store, MixSwitcher switcher,
                          DialogManager? dialogs = null, MixContentResolver? content = null)
    {
        _store = store;
        _switcher = switcher;
        _dialogs = dialogs;
        _content = content;
        EntrySources = SourceCatalog.All.Where(f => f is IContentSourceFactory).ToList();
        StationOptions = new ObservableCollection<string>(new[] { UseGlobalDefault }.Concat(StationCatalog.Names));

        _store.Changed += OnStoreChanged;
        RefreshList();
    }

    /// <summary>Designer-only ctor.</summary>
    public MixesViewModel() : this(
        new MixStore(),
        DesignerSwitcher())
    { }

    private static MixSwitcher DesignerSwitcher()
    {
        var runner = new SourceRunner(new NullSink());
        var config = new SourceConfigStore();
        var queue = new QueuePlayback(runner, config, new MixContentResolver(config));
        return new MixSwitcher(new MixStore(), queue, runner);
    }

    private sealed class NullSink : IPcmSink
    {
        public bool Send(ReadOnlySpan<short> samples) => false;
    }

    private void OnStoreChanged() => Dispatcher.UIThread.Post(RefreshList);

    private void RefreshList()
    {
        Mixes.Clear();
        foreach (var m in _store.All) Mixes.Add(ToRow(m));
        OnPropertyChanged(nameof(HasMixes));
    }

    private MixRow ToRow(Mix m)
    {
        var id = m.Id;
        var station = m.Station == null ? "" : $"Station: {m.Station}";
        var row = new MixRow(id, m.Name, BuildSummary(m), station)
        {
            PlayCommand = new RelayCommand(() => PlayMix(id)),
            EditCommand = new RelayCommand(() => EditMix(id)),
            DeleteCommand = new RelayCommand(() => DeleteMix(id)),
        };
        if (m.Entries.Count > 0) _ = ResolveSummaryAsync(row, m);
        return row;
    }

    private string BuildSummary(Mix m)
    {
        var n = m.Entries.Count;
        if (n == 0) return "No entries";
        var first = m.Entries[0];
        var label = _entryTitles.TryGetValue(EntryKey(first), out var t) ? t : SummariseEntry(first);
        return $"{n} entr{(n == 1 ? "y" : "ies")} · {label}{(n > 1 ? " …" : "")}";
    }

    // Resolve the first entry to a real title (a flat-playlist title for YouTube, a
    // file/tag title for local) so the row shows that instead of the raw URL. Cheap,
    // cached, and lazy — failures keep the locator-based summary.
    private async Task ResolveSummaryAsync(MixRow row, Mix m)
    {
        if (_content == null) return;
        var first = m.Entries[0];
        var key = EntryKey(first);
        if (_entryTitles.ContainsKey(key)) return;
        await ResolveGate.WaitAsync().ConfigureAwait(false);
        try
        {
            if (_entryTitles.ContainsKey(key)) return; // may have resolved while we waited
            var items = await _content.EnumerateAsync(first, System.Threading.CancellationToken.None)
                .ConfigureAwait(false);
            if (items.Count == 0 || string.IsNullOrWhiteSpace(items[0].Metadata.Title)) return;
            var src = SourceCatalog.Find(first.SourceId)?.DisplayName ?? first.SourceId;
            _entryTitles[key] = $"{src}: {items[0].Metadata.Title}";
            Dispatcher.UIThread.Post(() => row.Summary = BuildSummary(m));
        }
        catch (Exception ex) { Debug.WriteLine($"[hzn-mixes-vm] resolve summary: {ex.Message}"); }
        finally { ResolveGate.Release(); }
    }

    private static string EntryKey(ContentRef e) => $"{e.SourceId}|{e.Locator}";

    private static string SummariseEntry(ContentRef e)
    {
        var source = SourceCatalog.Find(e.SourceId)?.DisplayName ?? e.SourceId;
        return $"{source}: {e.DisplayName ?? e.Locator}";
    }

    /// <summary>First available entry source (Local/YouTube always exist), or null.</summary>
    private IAudioSourceFactory? FirstSource => EntrySources.Count > 0 ? EntrySources[0] : null;

    [RelayCommand]
    private void New()
    {
        _editingId = null;
        EditingName = "";
        EditingStation = UseGlobalDefault;
        StatusMessage = "";
        HasError = false;
        EditEntries.Clear();
        AddEntry(); // start with one blank row
        IsEditing = true;
    }

    [RelayCommand]
    private void Cancel()
    {
        IsEditing = false;
        EditEntries.Clear();
    }

    [RelayCommand]
    private void AddEntry() => EditEntries.Add(NewEntryRow(FirstSource, ""));

    // Rows carry their own remove/reorder commands (closing over these helpers) so
    // the item template binds straight to the row instead of reaching up the tree.
    private MixEntryEditRow NewEntryRow(IAudioSourceFactory? source, string locator)
        => new(EntrySources, source, locator, RemoveEntryRow, MoveEntryUpRow, MoveEntryDownRow);

    private void RemoveEntryRow(MixEntryEditRow row) => EditEntries.Remove(row);

    private void MoveEntryUpRow(MixEntryEditRow row)
    {
        var i = EditEntries.IndexOf(row);
        if (i > 0) EditEntries.Move(i, i - 1);
    }

    private void MoveEntryDownRow(MixEntryEditRow row)
    {
        var i = EditEntries.IndexOf(row);
        if (i >= 0 && i < EditEntries.Count - 1) EditEntries.Move(i, i + 1);
    }

    [RelayCommand]
    private void Save()
    {
        if (string.IsNullOrWhiteSpace(EditingName)) { Fail("Give the mix a name."); return; }

        var entries = new List<ContentRef>();
        foreach (var row in EditEntries)
        {
            if (row.SelectedSource is null || string.IsNullOrWhiteSpace(row.Locator)) continue;
            entries.Add(new ContentRef(row.SelectedSource.Id, row.Locator.Trim()));
        }
        if (entries.Count == 0) { Fail("Add at least one entry (a source and a URL/folder)."); return; }

        var station = EditingStation == UseGlobalDefault ? null : EditingStation;
        var id = _editingId ?? Guid.NewGuid().ToString("n");
        _store.AddOrUpdate(new Mix(id, EditingName.Trim(), entries, station));
        _store.SaveToDisk();

        IsEditing = false;
        EditEntries.Clear();
        StatusMessage = "";
        HasError = false;
    }

    private void EditMix(string id)
    {
        var mix = _store.Get(id);
        if (mix is null) return;

        _editingId = mix.Id;
        EditingName = mix.Name;
        EditingStation = mix.Station ?? UseGlobalDefault;
        StatusMessage = "";
        HasError = false;

        EditEntries.Clear();
        foreach (var e in mix.Entries)
        {
            var source = EntrySources.FirstOrDefault(f => f.Id == e.SourceId) ?? FirstSource;
            EditEntries.Add(NewEntryRow(source, e.Locator));
        }
        if (EditEntries.Count == 0) AddEntry();

        IsEditing = true;
    }

    private void DeleteMix(string id)
    {
        _store.Remove(id);
        _store.SaveToDisk();
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
            Debug.WriteLine($"[hzn-mixes-vm] play failed: {ex}");
        }
    }

    private void Fail(string message)
    {
        HasError = true;
        StatusMessage = message;
    }
}

/// <summary>A row in the Mixes list. Carries its own action commands; Summary is
/// observable so resolved entry titles can replace the raw locator in place.</summary>
public sealed partial class MixRow : ViewModelBase
{
    public string Id { get; }
    public string Name { get; }
    public string Station { get; }
    public bool HasStation => !string.IsNullOrEmpty(Station);

    [ObservableProperty] private string summary;

    public required ICommand PlayCommand { get; init; }
    public required ICommand EditCommand { get; init; }
    public required ICommand DeleteCommand { get; init; }

    public MixRow(string id, string name, string summary, string station)
    {
        Id = id;
        Name = name;
        this.summary = summary;
        Station = station;
    }
}

/// <summary>An editable entry row in the mix builder: a source + a locator,
/// plus its own remove/reorder commands.</summary>
public sealed partial class MixEntryEditRow : ViewModelBase
{
    public IReadOnlyList<IAudioSourceFactory> Sources { get; }

    [ObservableProperty] private IAudioSourceFactory? selectedSource;
    [ObservableProperty] private string locator;

    public ICommand RemoveCommand { get; }
    public ICommand MoveUpCommand { get; }
    public ICommand MoveDownCommand { get; }

    public MixEntryEditRow(
        IReadOnlyList<IAudioSourceFactory> sources,
        IAudioSourceFactory? selected,
        string locator,
        Action<MixEntryEditRow> remove,
        Action<MixEntryEditRow> moveUp,
        Action<MixEntryEditRow> moveDown)
    {
        Sources = sources;
        selectedSource = selected;
        this.locator = locator;
        RemoveCommand = new RelayCommand(() => remove(this));
        MoveUpCommand = new RelayCommand(() => moveUp(this));
        MoveDownCommand = new RelayCommand(() => moveDown(this));
    }

    /// <summary>Placeholder hint for the locator box, following the selected source.</summary>
    public string LocatorHint => (SelectedSource as IContentSourceFactory)?.LocatorHint ?? "URL, folder, or file";

    partial void OnSelectedSourceChanged(IAudioSourceFactory? value) => OnPropertyChanged(nameof(LocatorHint));
}
