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

    public MixesViewModel(MixStore store, MixSwitcher switcher)
    {
        _store = store;
        _switcher = switcher;
        EntrySources = SourceCatalog.All.Where(f => f is IContentSourceFactory).ToList();
        StationOptions = new ObservableCollection<string>(new[] { UseGlobalDefault }.Concat(StationCatalog.Names));

        _store.Changed += OnStoreChanged;
        RefreshList();
    }

    /// <summary>Designer-only ctor.</summary>
    public MixesViewModel() : this(
        new MixStore(),
        new MixSwitcher(new MixStore(), new SourceConfigStore(), new SourceRunner(new NullSink())))
    { }

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
        var n = m.Entries.Count;
        var summary = n == 0
            ? "No entries"
            : $"{n} entr{(n == 1 ? "y" : "ies")} · {SummariseEntry(m.Entries[0])}{(n > 1 ? " …" : "")}";
        var station = m.Station == null ? "" : $"Station: {m.Station}";
        return new MixRow(id, m.Name, summary, station)
        {
            PlayCommand = new AsyncRelayCommand(() => PlayAsync(id)),
            EditCommand = new RelayCommand(() => EditMix(id)),
            DeleteCommand = new RelayCommand(() => DeleteMix(id)),
        };
    }

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

    private async Task PlayAsync(string id)
    {
        var mix = _store.Get(id);
        if (mix is null) return;

        HasError = false;
        StatusMessage = $"Starting {mix.Name}…";
        try
        {
            await _switcher.SwitchToAsync(id);
            StatusMessage = $"Playing: {mix.Name}";
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

/// <summary>A row in the Mixes list. Carries its own action commands.</summary>
public sealed class MixRow(string id, string name, string summary, string station)
{
    public string Id { get; } = id;
    public string Name { get; } = name;
    public string Summary { get; } = summary;
    public string Station { get; } = station;
    public bool HasStation => !string.IsNullOrEmpty(Station);

    public required ICommand PlayCommand { get; init; }
    public required ICommand EditCommand { get; init; }
    public required ICommand DeleteCommand { get; init; }
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
