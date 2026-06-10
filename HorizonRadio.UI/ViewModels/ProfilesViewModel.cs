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
using HorizonRadio.Core.Sources.Profiles;
using HorizonRadio.UI.Tools;

namespace HorizonRadio.UI.ViewModels;

/// <summary>
/// Profiles tab: a flat list of saved source presets the user can Play, Edit,
/// or Delete, plus an inline editor (name + source + content fields) for
/// creating/editing one. Each profile bundles a source and its content config;
/// switching to it starts the runner with the merged config via
/// <see cref="ProfileLauncher"/>.
/// </summary>
public sealed partial class ProfilesViewModel : ViewModelBase
{
    private readonly SourceProfileStore _store;
    private readonly ProfileSwitcher _switcher;
    private readonly ToolRegistry? _registry;

    public ObservableCollection<ProfileRow> Profiles { get; } = new();
    public ObservableCollection<IAudioSourceFactory> AvailableSources { get; } = new();
    public ObservableCollection<ConfigFieldViewModel> EditFields { get; } = new();

    [ObservableProperty] private bool isEditing;
    [ObservableProperty] private string editingName = "";
    [ObservableProperty] private IAudioSourceFactory? editingSource;
    [ObservableProperty] private string statusMessage = "";
    [ObservableProperty] private bool hasError;
    public bool HasProfiles => Profiles.Count > 0;

    // null = creating a new profile; otherwise the id being edited.
    private string? _editingId;
    // Content to seed the editor fields with (the profile being edited), and the
    // source it belongs to — the seed is only applied while that source is
    // selected, so switching the source dropdown doesn't bleed its values in.
    private IReadOnlyDictionary<string, object?>? _editSeed;
    private string? _editSeedSourceId;

    public ProfilesViewModel(
        SourceProfileStore store,
        ProfileSwitcher switcher,
        ToolRegistry? registry = null)
    {
        _store = store;
        _switcher = switcher;
        _registry = registry;

        foreach (var f in SourceCatalog.All) AvailableSources.Add(f);

        _store.Changed += OnStoreChanged;
        RefreshList();
    }

    /// <summary>Designer-only ctor.</summary>
    public ProfilesViewModel() : this(
        new SourceProfileStore(),
        new ProfileSwitcher(new SourceProfileStore(), new SourceConfigStore(), new SourceRunner(new NullSink())))
    { }

    private sealed class NullSink : IPcmSink
    {
        public bool Send(ReadOnlySpan<short> samples) => false;
    }

    private void OnStoreChanged() => Dispatcher.UIThread.Post(RefreshList);

    private void RefreshList()
    {
        Profiles.Clear();
        foreach (var p in _store.All) Profiles.Add(ToRow(p));
        OnPropertyChanged(nameof(HasProfiles));
    }

    // Each row carries its own commands (closing over the id) so the list item
    // template binds straight to the row — no reach-up to the parent VM.
    private ProfileRow ToRow(SourceProfile p)
    {
        var sourceName = SourceCatalog.Find(p.SourceId)?.DisplayName ?? p.SourceId;
        // One-line summary: the first non-empty text value (URL, folder, …).
        var summary = p.Content.Values.OfType<string>().FirstOrDefault(s => !string.IsNullOrWhiteSpace(s)) ?? "";
        var id = p.Id;
        return new ProfileRow(id, p.Name, sourceName, summary)
        {
            PlayCommand = new AsyncRelayCommand(() => PlayAsync(id)),
            EditCommand = new RelayCommand(() => EditProfile(id)),
            DeleteCommand = new RelayCommand(() => DeleteProfile(id)),
        };
    }

    [RelayCommand]
    private void New()
    {
        _editingId = null;
        _editSeed = null;
        _editSeedSourceId = null;
        EditingName = "";
        StatusMessage = "";
        HasError = false;
        EditingSource = AvailableSources.FirstOrDefault();
        RebuildEditFields(); // covers the case where the source was already first
        IsEditing = true;
    }

    [RelayCommand]
    private void Cancel()
    {
        IsEditing = false;
        EditFields.Clear();
    }

    [RelayCommand]
    private void Save()
    {
        if (EditingSource is null) { Fail("Pick a source."); return; }
        if (string.IsNullOrWhiteSpace(EditingName)) { Fail("Give the profile a name."); return; }

        var content = new Dictionary<string, object?>();
        foreach (var f in EditFields) content[f.Key] = f.GetValue();

        var id = _editingId ?? Guid.NewGuid().ToString("n");
        _store.AddOrUpdate(new SourceProfile(id, EditingName.Trim(), EditingSource.Id, content));
        _store.SaveToDisk();

        IsEditing = false;
        EditFields.Clear();
        StatusMessage = "";
        HasError = false;
    }

    private void EditProfile(string id)
    {
        var profile = _store.Get(id);
        if (profile is null) return;

        _editingId = profile.Id;
        _editSeed = profile.Content;
        _editSeedSourceId = profile.SourceId;
        EditingName = profile.Name;
        StatusMessage = "";
        HasError = false;
        EditingSource = SourceCatalog.Find(profile.SourceId) ?? AvailableSources.FirstOrDefault();
        RebuildEditFields();
        IsEditing = true;
    }

    private void DeleteProfile(string id)
    {
        _store.Remove(id);
        _store.SaveToDisk();
    }

    private async Task PlayAsync(string id)
    {
        var profile = _store.Get(id);
        if (profile is null) return;

        HasError = false;
        StatusMessage = $"Starting {profile.Name}…";
        try
        {
            await _switcher.SwitchToAsync(id);
            StatusMessage = $"Playing: {profile.Name}";
        }
        catch (Exception ex)
        {
            Fail(ex.Message);
            Debug.WriteLine($"[hzn-profiles-vm] play failed: {ex}");
        }
    }

    partial void OnEditingSourceChanged(IAudioSourceFactory? value) => RebuildEditFields();

    private void RebuildEditFields()
    {
        EditFields.Clear();
        if (EditingSource is null) return;

        // Only seed from the saved profile while its own source is selected;
        // switching to another source rebuilds from defaults instead of bleeding
        // same-keyed values across sources.
        var useSeed = _editSeed != null && EditingSource.Id == _editSeedSourceId;
        foreach (var field in EditingSource.Schema.Where(ProfileLauncher.IsContentField))
        {
            var fvm = ConfigFieldViewModel.For(field, _registry);
            if (useSeed && _editSeed!.TryGetValue(field.Key, out var v)) fvm.SetValue(v);
            EditFields.Add(fvm);
        }
    }

    private void Fail(string message)
    {
        HasError = true;
        StatusMessage = message;
    }
}

/// <summary>A row in the Profiles list. Carries its own action commands.</summary>
public sealed class ProfileRow(string id, string name, string sourceDisplay, string summary)
{
    public string Id { get; } = id;
    public string Name { get; } = name;
    public string SourceDisplay { get; } = sourceDisplay;
    public string Summary { get; } = summary;
    public bool HasSummary => !string.IsNullOrWhiteSpace(Summary);

    public required ICommand PlayCommand { get; init; }
    public required ICommand EditCommand { get; init; }
    public required ICommand DeleteCommand { get; init; }
}
