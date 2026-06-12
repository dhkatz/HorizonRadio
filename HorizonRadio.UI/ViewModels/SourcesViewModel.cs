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

    /// <summary>True when the selected source is content-addressable. Its engine
    /// (tool paths, behavior) is configured here; "what to play" is a transient
    /// quick-play locator (below) or — for saved collections — a mix.</summary>
    public bool IsContentSource => SelectedFactory is IContentSourceFactory;

    /// <summary>Whether the selected source can be started/played from here.</summary>
    public bool CanStartSelected => SelectedFactory is not null;

    /// <summary>Ad-hoc "play this now" target for a content source — a URL, folder,
    /// M3U, or file. Transient: it's passed to the source for this play but not
    /// saved as config (saved collections are mixes).</summary>
    [ObservableProperty] private string quickPlayLocator = "";

    /// <summary>Placeholder for the quick-play box, following the selected source.</summary>
    public string QuickPlayHint => SelectedFactory?.Id switch
    {
        "youtube" => "https://youtube.com/watch?v=… or /playlist?list=…",
        "local" => @"Folder, M3U, or file (e.g. C:\Music)",
        _ => "URL, folder, or file",
    };

    /// <summary>Start/Play button label — content sources "Play" the quick-play
    /// locator; self-driven sources just "Start".</summary>
    public string StartLabel => IsContentSource ? "Play" : "Start";
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
        OnPropertyChanged(nameof(QuickPlayHint));
        OnPropertyChanged(nameof(StartLabel));
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

        // Engine config is snapshotted/persisted; the quick-play locator is layered
        // on transiently (content sources only) so it plays now without being saved.
        var values = SnapshotAndPersist();
        if (SelectedFactory is IContentSourceFactory csf && !string.IsNullOrWhiteSpace(QuickPlayLocator))
            values.Set(csf.ContentKey, QuickPlayLocator.Trim());

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
