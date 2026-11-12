using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Linq;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using HorizonRadio.Core.Playback;
using HorizonRadio.Core.Sources;
using HorizonRadio.Core.Sources.Config;
using HorizonRadio.Core.Sources.Mixes;
using HorizonRadio.Core.Sources.Queue;
using HorizonRadio.Sdk.Sources;
using HorizonRadio.Sdk.Sources.Config;
using HorizonRadio.UI.Tools;

namespace HorizonRadio.UI.ViewModels;

/// <summary>
/// Sources tab: pick a source from the catalog, fill in its config
/// (auto-rendered from the factory's schema), then Play or Start it. A content
/// source plays its quick-play locator through the queue, like quick play and
/// search; a self-driven source starts through the playback router, which owns the
/// engine. Persists the last-selected source, and each field as it's edited.
/// </summary>
public sealed partial class SourcesViewModel : ViewModelBase
{
    private readonly PlaybackRouter _playback;
    private readonly SourceRunner _runner;
    private readonly SourceConfigStore _store;
    private readonly QueuePlayback _queue;
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

    /// <summary>True when the selected source needs an account — surfaces the generic
    /// Connect / Disconnect panel (any <see cref="IAuthenticatingSource"/>).</summary>
    public bool IsAuthenticatingSource => SelectedFactory is IAuthenticatingSource;

    /// <summary>Whether the selected authenticating source's account is connected.</summary>
    [ObservableProperty] private bool isConnected;

    /// <summary>Status line for the connect panel (redirect URI, errors, etc.).</summary>
    [ObservableProperty] private string connectionStatus = "";

    /// <summary>Ad-hoc "play this now" target for a content source — a URL, folder,
    /// M3U, or file. The only locator Play uses (the content field isn't part of the
    /// form or the saved config); transient, since saved collections are mixes.</summary>
    [ObservableProperty] private string quickPlayLocator = "";

    /// <summary>Placeholder for the quick-play box, following the selected source.</summary>
    public string QuickPlayHint => (SelectedFactory as IContentSourceFactory)?.LocatorHint ?? "URL, folder, or file";

    /// <summary>Start/Play button label — content sources "Play" the quick-play
    /// locator; self-driven sources just "Start".</summary>
    public string StartLabel => IsContentSource ? "Play" : "Start";
    [ObservableProperty] private string statusMessage = "";
    [ObservableProperty] private bool hasError;
    [ObservableProperty] private bool hasNoSchema;

    public SourcesViewModel(PlaybackRouter playback, SourceConfigStore store, ToolRegistry? registry = null)
    {
        _playback = playback;
        _runner = playback.Runner;
        _store = store;
        _queue = playback.Player;
        _registry = registry;

        foreach (var f in SourceCatalog.All) AvailableSources.Add(f);

        _runner.ActiveSourceChanged += _ =>
            Avalonia.Threading.Dispatcher.UIThread.Post(() => IsRunning = _runner.IsRunning);

        var initial = SourceCatalog.Find(store.Settings.Get(SourceSettings.LastSelected) ?? "")
                   ?? AvailableSources.FirstOrDefault();
        SelectedFactory = initial;
    }

    /// <summary>Designer-only ctor (so Avalonia previewer can construct
    /// the view without a runner).</summary>
    public SourcesViewModel() : this(new SourceConfigStore())
    { }

    private SourcesViewModel(SourceConfigStore store)
        : this(new PlaybackRouter(new SourceRunner(new NullSink()), store, new MixStore(), new MixContentResolver(store)),
               store)
    { }

    private sealed class NullSink : Sdk.Sources.IPcmSink
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
        OnPropertyChanged(nameof(IsAuthenticatingSource));
        RefreshConnection();
        _store.Settings.Set(SourceSettings.LastSelected, value?.Id);
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
            // Save as the user edits: a setting other code reads (the Soloist key, a tool
            // path) must take effect without pressing Play here first.
            fvm.PropertyChanged += (_, _) =>
            {
                if (ReferenceEquals(SelectedFactory, factory)) SnapshotAndPersist();
            };
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
        }
        return values;
    }

    [RelayCommand]
    private async Task StartAsync()
    {
        if (SelectedFactory == null) return;
        var factory = SelectedFactory;
        HasError = false;

        // Engine config (tool paths, behavior) is snapshotted and persisted either way.
        var values = SnapshotAndPersist();

        try
        {
            switch (factory)
            {
                case IContentSourceFactory content:
                    await PlayContentAsync(content);
                    break;
                case ISelfDrivenSourceFactory selfDriven:
                    StatusMessage = "Starting...";
                    await _playback.StartSourceAsync(selfDriven, values);
                    StatusMessage = $"Running: {factory.DisplayName}";
                    break;
            }
        }
        catch (Exception ex)
        {
            HasError = true;
            StatusMessage = ex.Message;
            Trace.WriteLine($"[hzn-sources-vm] start failed: {ex}");
        }
    }

    /// <summary>Play the quick-play locator now through the queue — the same path as
    /// quick play and search: its items go to the front of the queue and start
    /// playing, and whatever was playing (or queued ahead) gives way.</summary>
    private async Task PlayContentAsync(IContentSourceFactory content)
    {
        var locator = QuickPlayLocator.Trim();
        if (locator.Length == 0)
        {
            HasError = true;
            StatusMessage = $"Enter something to play: {content.LocatorHint}";
            return;
        }

        StatusMessage = "Adding to the queue...";
        await _queue.EnqueueLocatorAsync(content, locator, playNow: true);
        StatusMessage = $"Playing from {content.DisplayName}";
    }

    [RelayCommand]
    private async Task StopAsync()
    {
        StatusMessage = "Stopping...";
        try { await _playback.StopAsync(); }
        catch (Exception ex) { Trace.WriteLine($"[hzn-sources-vm] stop: {ex}"); }
        StatusMessage = "Stopped";
        HasError = false;
    }

    // -- Account connect (any IAuthenticatingSource — Spotify today, others later) --

    private void RefreshConnection()
    {
        if (SelectedFactory is IAuthenticatingSource auth)
        {
            IsConnected = auth.IsConnected;
            ConnectionStatus = auth.StatusText;
        }
        else
        {
            IsConnected = false;
            ConnectionStatus = "";
        }
    }

    [RelayCommand]
    private async Task ConnectAsync()
    {
        if (SelectedFactory is not IAuthenticatingSource auth) return;

        // Persist the form first so any just-typed config (e.g. a Client ID) is
        // captured before the connect flow reads it.
        var factory = SelectedFactory;
        var values = SnapshotAndPersist();
        ConnectionStatus = "Opening browser…";
        try
        {
            await auth.ConnectAsync(values);
            // The browser handshake can take many seconds; only touch the panel if the
            // user hasn't switched to a different source in the meantime.
            if (ReferenceEquals(SelectedFactory, factory)) RefreshConnection();
        }
        catch (Exception ex)
        {
            if (ReferenceEquals(SelectedFactory, factory))
            {
                IsConnected = false;
                ConnectionStatus = $"Connect failed: {ex.Message}";
            }
            Trace.WriteLine($"[hzn-sources-vm] connect: {ex}");
        }
    }

    [RelayCommand]
    private void Disconnect()
    {
        (SelectedFactory as IAuthenticatingSource)?.Disconnect();
        RefreshConnection();
    }
}
