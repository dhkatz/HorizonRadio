using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Linq;
using System.Windows.Input;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using HorizonRadio.Core.Metadata;
using HorizonRadio.Sdk.Metadata;
using HorizonRadio.Sdk.Sources.Config;

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
    private readonly MetadataPipeline? _pipeline;

    /// <summary>Providers in priority order (enabled first, then the rest). The user
    /// reorders and toggles these.</summary>
    public ObservableCollection<MetadataProviderRow> Providers { get; } = new();

    /// <summary>Per-field source overrides (Title/Artist/Album/Art/Year).</summary>
    public ObservableCollection<MetadataFieldRow> Fields { get; } = new();

    [ObservableProperty] private string statusMessage = "";
    [ObservableProperty] private bool hasError;

    public MetadataViewModel(MetadataConfigStore store, MetadataPipeline pipeline)
    {
        _store = store;
        _pipeline = pipeline;
        BuildProviders();
        BuildFields();
        // The order and overrides sync between your devices (Devices): when they change
        // elsewhere, the page shows them (the pipeline puts them to use).
        _ = store.Settings.Watch(MetadataSettings.Order, _ => Dispatcher.UIThread.Post(Reload));
        _ = store.Settings.Watch(MetadataSettings.Forced, _ => Dispatcher.UIThread.Post(Reload));
    }

    private IReadOnlyList<string> _shownOrder = [];
    private Dictionary<MetadataField, string> _shownForced = [];

    private void Reload()
    {
        if (_store.Order.SequenceEqual(_shownOrder) && _store.Forced.Count == _shownForced.Count
            && !_store.Forced.Except(_shownForced).Any())
            return; // what this page applied itself
        Providers.Clear();
        Fields.Clear();
        BuildProviders();
        BuildFields();
    }

    /// <summary>Designer ctor.</summary>
    public MetadataViewModel()
    {
        _store = new MetadataConfigStore();
        _pipeline = null;
        BuildProviders();
        BuildFields();
    }

    private void BuildProviders()
    {
        _shownOrder = [.. _store.Order];
        // Enabled providers first, in saved order; then any remaining (disabled).
        var ordered = new List<IMetadataProviderFactory>();
        foreach (var id in _store.Order)
            if (_store.Catalog.Find(id) is { } f && !ordered.Contains(f)) ordered.Add(f);
        foreach (var f in _store.Catalog.All)
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
        _shownForced = new Dictionary<MetadataField, string>(_store.Forced);
        // Option set shared by every field: Auto + Source + each provider.
        var providerOptions = _store.Catalog.All
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
        if (_pipeline == null) return;
        HasError = false;
        StatusMessage = "Applying...";
        try
        {
            // Persist every provider's config (so creds stick even when disabled),
            // and the enabled ones — in their current display order — as the chain.
            var settings = new List<(string, ConfigValues)>();
            var order = new List<string>();
            foreach (var row in Providers)
            {
                var values = new ConfigValues();
                foreach (var f in row.Fields) values.Set(f.Key, f.GetValue());
                settings.Add((row.Id, values));
                if (row.IsEnabled) order.Add(row.Id);
            }

            var forced = new Dictionary<MetadataField, string>();
            foreach (var fr in Fields)
                if (fr.Selected?.ProviderId is { } pid) forced[fr.Field] = pid;
            _shownOrder = [.. order];
            _shownForced = new Dictionary<MetadataField, string>(forced);
            _pipeline.Save(settings, order, forced);

            StatusMessage = _store.Order.Count == 0
                ? "Enrichment disabled (no providers enabled)."
                : $"Active: {string.Join(" → ", _store.Order)}.";
        }
        catch (Exception ex)
        {
            HasError = true;
            StatusMessage = ex.Message;
            Trace.WriteLine($"[hzn-meta-vm] apply: {ex}");
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
