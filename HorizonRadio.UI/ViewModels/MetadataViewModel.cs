using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Linq;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using HorizonRadio.Core.Metadata;
using HorizonRadio.Core.Sources.Config;

namespace HorizonRadio.UI.ViewModels;

/// <summary>
/// Metadata tab: pick which enrichment provider runs after each track
/// change (Spotify, MusicBrainz, or none) and configure its
/// credentials. Reuses the same <see cref="ConfigFieldViewModel"/>
/// rendering pipeline as the Sources tab.
/// </summary>
public sealed partial class MetadataViewModel : ViewModelBase
{
    private sealed class NoneProvider : IMetadataProviderFactory
    {
        public string Id => MetadataCatalog.NoneId;
        public string DisplayName => "None (no enrichment)";
        public string? Description => "Now Playing shows only what the source provides. No network calls, no API keys needed.";
        public IReadOnlyList<ConfigField> Schema { get; } = Array.Empty<ConfigField>();
        public IMetadataProvider Create(ConfigValues v, MetadataCache c) =>
            throw new InvalidOperationException("NoneProvider should never be Created");
    }

    private readonly MetadataConfigStore _store;
    private readonly MetadataCache _cache;
    private readonly EnrichmentService _service;

    public ObservableCollection<IMetadataProviderFactory> AvailableProviders { get; } = new();
    public ObservableCollection<ConfigFieldViewModel> CurrentSchema { get; } = new();

    [ObservableProperty] private IMetadataProviderFactory? selectedProvider;
    [ObservableProperty] private string statusMessage = "";
    [ObservableProperty] private bool hasError;
    [ObservableProperty] private bool hasNoSchema;

    public MetadataViewModel(MetadataConfigStore store,
                             MetadataCache cache,
                             EnrichmentService service)
    {
        _store = store;
        _cache = cache;
        _service = service;

        AvailableProviders.Add(new NoneProvider());
        foreach (var f in MetadataCatalog.All) AvailableProviders.Add(f);

        var lastId = store.SelectedProviderId ?? MetadataCatalog.NoneId;
        var initial = AvailableProviders.FirstOrDefault(p => p.Id == lastId)
                   ?? AvailableProviders[0];
        SelectedProvider = initial;

        Apply();
    }

    /// <summary>Designer ctor.</summary>
    public MetadataViewModel()
    {
        AvailableProviders.Add(new NoneProvider());
        _store = new MetadataConfigStore();
        _cache = new MetadataCache();
        _service = null!;
    }

    partial void OnSelectedProviderChanged(IMetadataProviderFactory? value)
    {
        RebuildSchema(value);
    }

    private void RebuildSchema(IMetadataProviderFactory? factory)
    {
        CurrentSchema.Clear();
        if (factory == null || factory.Schema.Count == 0)
        {
            HasNoSchema = true;
            return;
        }

        var values = _store.Load(factory.Id, factory.Schema);
        var stored = values.AsReadOnly();
        foreach (var field in factory.Schema)
        {
            var fvm = ConfigFieldViewModel.For(field);
            if (stored.TryGetValue(field.Key, out var v)) fvm.SetValue(v);
            CurrentSchema.Add(fvm);
        }
        HasNoSchema = false;
    }

    [RelayCommand]
    private void Apply()
    {
        if (SelectedProvider == null || _service == null) return;
        HasError = false;
        StatusMessage = "Applying...";

        try
        {
            if (SelectedProvider.Id == MetadataCatalog.NoneId)
            {
                _service.SetProvider(null);
                _store.SelectedProviderId = MetadataCatalog.NoneId;
                _store.SaveToDisk();
                StatusMessage = "Enrichment disabled.";
                return;
            }

            var values = new ConfigValues();
            foreach (var f in CurrentSchema) values.Set(f.Key, f.GetValue());
            _store.Save(SelectedProvider.Id, values);
            _store.SelectedProviderId = SelectedProvider.Id;
            _store.SaveToDisk();

            var provider = SelectedProvider.Create(values, _cache);
            _service.SetProvider(provider);
            StatusMessage = $"{SelectedProvider.DisplayName} active.";
        }
        catch (Exception ex)
        {
            HasError = true;
            StatusMessage = ex.Message;
            Debug.WriteLine($"[hzn-meta-vm] apply: {ex}");
        }
    }
}
