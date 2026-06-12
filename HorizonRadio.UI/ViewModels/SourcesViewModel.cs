using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Linq;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using HorizonRadio.Core.Sources;
using HorizonRadio.Core.Sources.Config;
using HorizonRadio.UI.Tools;

namespace HorizonRadio.UI.ViewModels;

/// <summary>
/// Sources tab: pick a source from the catalog, fill in its config
/// (auto-rendered from the factory's schema), Start it. Persists the
/// last-selected source + last-used config across runs.
/// </summary>
public sealed partial class SourcesViewModel : ViewModelBase
{
    private readonly SourceRunner _runner;
    private readonly SourceConfigStore _store;
    private readonly ToolRegistry? _registry;

    public ObservableCollection<IAudioSourceFactory> AvailableSources { get; } = new();
    public ObservableCollection<ConfigFieldViewModel> CurrentSchema { get; } = new();

    [ObservableProperty] private IAudioSourceFactory? selectedFactory;
    [ObservableProperty] private bool isRunning;

    /// <summary>True when the selected source plays via mixes (content-addressable)
    /// rather than being started directly. The Sources tab configures its engine
    /// (tool paths, behavior); what to play is chosen in the Mixes tab.</summary>
    public bool IsContentSource => SelectedFactory is IContentSourceFactory;

    /// <summary>Only self-driven sources (Spotify Connect, the test tone) start
    /// from here — content sources need a mix to supply what to play.</summary>
    public bool CanStartSelected => SelectedFactory is not null and not IContentSourceFactory;
    [ObservableProperty] private string statusMessage = "";
    [ObservableProperty] private bool hasError;
    [ObservableProperty] private bool hasNoSchema;

    public SourcesViewModel(SourceRunner runner, SourceConfigStore store, ToolRegistry? registry = null)
    {
        _runner = runner;
        _store = store;
        _registry = registry;

        foreach (var f in SourceCatalog.All) AvailableSources.Add(f);

        _runner.ActiveSourceChanged += _ =>
            Avalonia.Threading.Dispatcher.UIThread.Post(() => IsRunning = _runner.IsRunning);

        var initial = SourceCatalog.Find(store.LastSelectedId ?? "")
                   ?? AvailableSources.FirstOrDefault();
        SelectedFactory = initial;
    }

    /// <summary>Designer-only ctor (so Avalonia previewer can construct
    /// the view without a runner).</summary>
    public SourcesViewModel() : this(
        new SourceRunner(new NullSink()),
        new SourceConfigStore())
    { }

    private sealed class NullSink : Core.Sources.IPcmSink
    {
        public bool Send(ReadOnlySpan<short> samples) => false;
    }

    partial void OnSelectedFactoryChanged(IAudioSourceFactory? value)
    {
        RebuildSchema(value);
        OnPropertyChanged(nameof(IsContentSource));
        OnPropertyChanged(nameof(CanStartSelected));
        _store.LastSelectedId = value?.Id;
        _store.SaveToDisk();
    }

    private void RebuildSchema(IAudioSourceFactory? factory)
    {
        CurrentSchema.Clear();
        if (factory == null) { HasNoSchema = false; return; }

        var values = _store.Load(factory.Id, factory.Schema);
        var stored = values.AsReadOnly();

        // The content locator (URL/folder) is no longer a per-source setting — it
        // lives in mixes. Show only the engine fields (tool paths, behavior).
        var contentKey = (factory as IContentSourceFactory)?.ContentKey;

        foreach (var field in factory.Schema)
        {
            if (field.Key == contentKey) continue;
            var fvm = ConfigFieldViewModel.For(field, _registry);
            if (stored.TryGetValue(field.Key, out var v)) fvm.SetValue(v);
            CurrentSchema.Add(fvm);
        }
        HasNoSchema = CurrentSchema.Count == 0;
    }

    /// <summary>Snapshot the current form into a ConfigValues + persist it.</summary>
    private ConfigValues SnapshotAndPersist()
    {
        var values = new ConfigValues();
        foreach (var f in CurrentSchema) values.Set(f.Key, f.GetValue());

        if (SelectedFactory != null)
        {
            _store.Save(SelectedFactory.Id, values);
            _store.SaveToDisk();
        }
        return values;
    }

    [RelayCommand]
    private async Task StartAsync()
    {
        if (SelectedFactory == null) return;
        HasError = false;
        StatusMessage = "Starting...";

        var values = SnapshotAndPersist();
        try
        {
            await _runner.StartAsync(SelectedFactory, values);
            StatusMessage = $"Running: {SelectedFactory.DisplayName}";
        }
        catch (Exception ex)
        {
            HasError = true;
            StatusMessage = ex.Message;
            Debug.WriteLine($"[hzn-sources-vm] start failed: {ex}");
        }
    }

    [RelayCommand]
    private async Task StopAsync()
    {
        StatusMessage = "Stopping...";
        try { await _runner.StopAsync(); }
        catch (Exception ex) { Debug.WriteLine($"[hzn-sources-vm] stop: {ex}"); }
        StatusMessage = "Stopped";
        HasError = false;
    }
}
