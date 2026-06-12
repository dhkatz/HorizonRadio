using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Linq;
using System.Windows.Input;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using HorizonRadio.Core.Metadata;
using HorizonRadio.Core.Sources.Config;

namespace HorizonRadio.UI.ViewModels;

/// <summary>
/// Metadata tab: enable and order the providers (MusicBrainz, Spotify, …) the
/// resolver consults after the source, set per-field overrides ("always take Art
/// from Spotify"), and enter any credentials. The order is priority; by default
/// MusicBrainz is on (no credentials needed) so enrichment works out of the box.
/// </summary>
public sealed partial class MetadataViewModel : ViewModelBase
{
    private readonly MetadataConfigStore _store;
    private readonly MetadataCache _cache;
    private readonly MetadataResolver _resolver;

    /// <summary>Providers in priority order (enabled first, then the rest). The user
    /// reorders and toggles these.</summary>
    public ObservableCollection<MetadataProviderRow> Providers { get; } = new();

    /// <summary>Per-field source overrides (Title/Artist/Album/Art/Year).</summary>
    public ObservableCollection<MetadataFieldRow> Fields { get; } = new();

    [ObservableProperty] private string statusMessage = "";
    [ObservableProperty] private bool hasError;

    public MetadataViewModel(MetadataConfigStore store, MetadataCache cache, MetadataResolver resolver)
    {
        _store = store;
        _cache = cache;
        _resolver = resolver;
        BuildProviders();
        BuildFields();
    }

    /// <summary>Designer ctor.</summary>
    public MetadataViewModel()
    {
        _store = new MetadataConfigStore();
        _cache = new MetadataCache();
        _resolver = null!;
        BuildProviders();
        BuildFields();
    }

    private void BuildProviders()
    {
        // Enabled providers first, in saved order; then any remaining (disabled).
        var ordered = new List<IMetadataProviderFactory>();
        foreach (var id in _store.Order)
            if (MetadataCatalog.Find(id) is { } f && !ordered.Contains(f)) ordered.Add(f);
        foreach (var f in MetadataCatalog.All)
            if (!ordered.Contains(f)) ordered.Add(f);

        foreach (var factory in ordered)
        {
            var fields = new ObservableCollection<ConfigFieldViewModel>();
            var values = _store.Load(factory.Id, factory.Schema).AsReadOnly();
            foreach (var field in factory.Schema)
            {
                var fvm = ConfigFieldViewModel.For(field);
                if (values.TryGetValue(field.Key, out var v)) fvm.SetValue(v);
                fields.Add(fvm);
            }
            Providers.Add(new MetadataProviderRow(
                factory.Id, factory.DisplayName, factory.Description,
                _store.Order.Contains(factory.Id), fields, MoveProvider));
        }
    }

    private void BuildFields()
    {
        // Option set shared by every field: Auto + Source + each provider.
        var providerOptions = MetadataCatalog.All
            .Select(f => new OverrideOption(f.DisplayName, f.Id))
            .ToList();

        foreach (var (field, label) in new[]
        {
            (MetadataField.Title, "Title"),
            (MetadataField.Artist, "Artist"),
            (MetadataField.Album, "Album"),
            (MetadataField.Art, "Album Art"),
            (MetadataField.Year, "Year"),
        })
        {
            var options = new ObservableCollection<OverrideOption>
            {
                new("Auto (use order)", null),
                new("Source", MetadataPolicy.SourceId),
            };
            foreach (var o in providerOptions) options.Add(o);

            var forcedId = _store.Forced.TryGetValue(field, out var id) ? id : null;
            var selected = options.FirstOrDefault(o => o.ProviderId == forcedId) ?? options[0];
            Fields.Add(new MetadataFieldRow(field, label, options, selected));
        }
    }

    // Reorder providers (move up = higher priority). Bound to each row's commands.
    private void MoveProvider(MetadataProviderRow row, int delta)
    {
        var i = Providers.IndexOf(row);
        if (i < 0) return;
        var j = Math.Clamp(i + delta, 0, Providers.Count - 1);
        if (j != i) Providers.Move(i, j);
    }

    [RelayCommand]
    private void Apply()
    {
        if (_resolver == null) return;
        HasError = false;
        StatusMessage = "Applying...";
        try
        {
            // Persist every provider's config (so creds stick even when disabled),
            // and the enabled ones — in their current display order — as the chain.
            _store.Order.Clear();
            foreach (var row in Providers)
            {
                var values = new ConfigValues();
                foreach (var f in row.Fields) values.Set(f.Key, f.GetValue());
                _store.Save(row.Id, values);
                if (row.IsEnabled) _store.Order.Add(row.Id);
            }

            _store.Forced.Clear();
            foreach (var fr in Fields)
                if (fr.Selected?.ProviderId is { } pid) _store.Forced[fr.Field] = pid;

            _store.SelectedProviderId = null; // superseded by Order
            _store.SaveToDisk();

            var (contributors, policy) = MetadataCatalog.BuildPipeline(_store, _cache);
            _resolver.Configure(contributors, policy);

            StatusMessage = _store.Order.Count == 0
                ? "Enrichment disabled (no providers enabled)."
                : $"Active: {string.Join(" → ", _store.Order)}.";
        }
        catch (Exception ex)
        {
            HasError = true;
            StatusMessage = ex.Message;
            Debug.WriteLine($"[hzn-meta-vm] apply: {ex}");
        }
    }
}

/// <summary>An orderable, toggleable provider in the Metadata tab, with its own
/// credential fields and reorder commands.</summary>
public sealed partial class MetadataProviderRow : ViewModelBase
{
    public string Id { get; }
    public string DisplayName { get; }
    public string? Description { get; }
    public ObservableCollection<ConfigFieldViewModel> Fields { get; }
    public bool HasFields => Fields.Count > 0;

    [ObservableProperty] private bool isEnabled;

    public ICommand MoveUpCommand { get; }
    public ICommand MoveDownCommand { get; }

    public MetadataProviderRow(
        string id, string displayName, string? description,
        bool isEnabled, ObservableCollection<ConfigFieldViewModel> fields,
        Action<MetadataProviderRow, int> move)
    {
        Id = id;
        DisplayName = displayName;
        Description = description;
        Fields = fields;
        this.isEnabled = isEnabled;
        MoveUpCommand = new RelayCommand(() => move(this, -1));
        MoveDownCommand = new RelayCommand(() => move(this, +1));
    }
}

/// <summary>A choice in a per-field override picker. Null <see cref="ProviderId"/>
/// means "Auto" (use the provider order).</summary>
public sealed record OverrideOption(string Label, string? ProviderId);

/// <summary>A per-field override row (e.g. Album Art → Spotify).</summary>
public sealed partial class MetadataFieldRow : ViewModelBase
{
    public MetadataField Field { get; }
    public string Label { get; }
    public ObservableCollection<OverrideOption> Options { get; }

    [ObservableProperty] private OverrideOption? selected;

    public MetadataFieldRow(MetadataField field, string label,
        ObservableCollection<OverrideOption> options, OverrideOption selected)
    {
        Field = field;
        Label = label;
        Options = options;
        this.selected = selected;
    }
}
