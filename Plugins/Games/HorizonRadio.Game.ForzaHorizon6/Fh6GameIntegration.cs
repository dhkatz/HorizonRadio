using System.ComponentModel;
using Avalonia.Threading;
using HorizonRadio.Core.Events;
using HorizonRadio.Core.Games;
using HorizonRadio.Core.Ipc;
using HorizonRadio.Core.Models;
using HorizonRadio.Core.ModInstall;
using HorizonRadio.Core.Sources;
using HorizonRadio.Game.ForzaHorizon6.ViewModels;
using HorizonRadio.Plugins.Abstractions;

namespace HorizonRadio.Game.ForzaHorizon6;

/// <summary>
/// The FH6 integration: the injected DLL's two named pipes (JSON control channel + PCM audio)
/// plus the optional Forza "Data Out" UDP telemetry feed as a second event source.
///
/// It owns the radio end to end. Each station plays the game's own music, the app's player, or a
/// mix (<see cref="StationMap"/>, edited on the Stations page). The DLL reports the tuned station;
/// we route the app's playback to match (<see cref="IPlaybackRouter"/>) and tell the DLL which
/// stations to replace, re-asserting both on every (re)connect.
/// </summary>
public sealed class Fh6GameIntegration : IGameIntegration, IStationTargeting, IGameVolume, INowPlayingDisplay
{
    private readonly IpcClient _ipc = new();
    private readonly PcmPipeClient _pcm = new();
    private readonly ForzaTelemetryListener _telemetry = new();
    private volatile int _state = (int)ConnectionState.Disconnected;
    private IReadOnlyList<GamePage>? _pages;
    private ModManagerViewModel? _modManager;

    private IGameHost? _host;
    private volatile string? _currentStation;

    /// <param name="displayName">The game's name.</param>
    /// <param name="settings">Where station assignments persist; <see langword="null"/> keeps them
    /// in memory only (tests).</param>
    public Fh6GameIntegration(string displayName, IPluginSettings? settings = null)
    {
        DisplayName = displayName;
        Map = new StationMap(settings);
        Map.Changed += OnMapChanged;
        Output = new PcmPipeSink(_pcm);
        EventSources = [_ipc, _telemetry];
        _ipc.Connected += () => SetState(ConnectionState.Connected);
        _ipc.Disconnected += () => SetState(ConnectionState.Disconnected);
        _ipc.StationChanged += OnStationChanged;
    }

    public string DisplayName { get; }

    /// <summary>Which station plays what (the Stations page edits this).</summary>
    public StationMap Map { get; }

    public IPcmSink Output { get; }

    public IReadOnlyList<IGameEventSource> EventSources { get; }

    public ConnectionState State => (ConnectionState)_state;

    public event Action<ConnectionState>? StateChanged;

    public string EventsHint =>
        $"For richer events, enable Forza “Data Out” in-game and point it at 127.0.0.1:{ForzaTelemetryListener.DefaultPort}.";

    public IStationTargeting Stations => this;

    public IGameVolume Volume => this;

    public INowPlayingDisplay NowPlaying => this;

    public void Start(IGameHost host)
    {
        _host = host;
        host.Playback.MixesChanged += OnMixesChanged;
        OnMixesChanged(); // drop assignments to mixes deleted while we weren't running
        SetState(ConnectionState.Connecting);
        _pcm.Start();
        _ipc.Start();
        _telemetry.Start();
    }

    /// <summary>The user's mixes (empty until <see cref="Start"/>).</summary>
    public IReadOnlyList<MixInfo> Mixes => _host?.Playback.Mixes ?? [];

    /// <summary>Raised when <see cref="Mixes"/> changes. May fire on any thread.</summary>
    public event Action? MixesChanged;

    // -- Pages + notice --

    public event Action? NoticeChanged;

    private const string ModsPageId = "mods";
    private const string StationsPageId = "stations";

    /// <summary>The Mods page (install / update the version.dll mod), the Stations page (what each
    /// station plays), and the Stats page (live audio-bridge telemetry). Built lazily: the mod
    /// manager scans for game installs and hashes the bundled DLL, and all need the UI thread.</summary>
    public IReadOnlyList<GamePage> Pages => _pages ??= BuildPages();

    public GameNotice? Notice => _modManager?.Status switch
    {
        InstallationStatus.InstalledOther => new GameNotice(
            "Mod Update Available", "A different version is installed in your game folder.", ModsPageId),
        InstallationStatus.NotInstalled => new GameNotice(
            "Mod Not Installed", "Horizon Radio isn't set up in your game yet.", ModsPageId),
        _ when _modManager != null && !Map.HasAssignments => new GameNotice(
            "No Station Plays Horizon Radio",
            "Every station plays the game's own music. Pick a station for your player or a mix.",
            StationsPageId),
        _ => null,
    };

    private IReadOnlyList<GamePage> BuildPages()
    {
        _modManager = new ModManagerViewModel();
        _modManager.PropertyChanged += OnModManagerChanged;

        var stations = new StationsViewModel(this);

        var stats = new StatsViewModel();
        // IPC events arrive on the pipe reader thread; the page binds on the UI thread.
        _ipc.StatsUpdated += s => Dispatcher.UIThread.Post(() => stats.Apply(s));
        _ipc.SourceChanged += s => Dispatcher.UIThread.Post(() => stats.SetSource(s.DisplayName));
        StateChanged += state => Dispatcher.UIThread.Post(() =>
            stats.SetConnectionState(state == ConnectionState.Connected));

        return
        [
            new GamePage(ModsPageId, "Mods", "PuzzleOutline", _modManager),
            new GamePage(StationsPageId, "Stations", "RadioTower", stations),
            new GamePage("stats", "Stats", "ChartBar", stats),
        ];
    }

    private void OnModManagerChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(ModManagerViewModel.Status))
            NoticeChanged?.Invoke();
    }

    // -- Stations --

    IReadOnlyList<string> IStationTargeting.Stations => StationCatalog.Names;

    public string? CurrentStation => _currentStation;

    public event Action? Changed;

    private void OnStationChanged(string station)
    {
        if (!StationCatalog.Names.Contains(station)) return; // a station we don't know: ignore
        _currentStation = station;
        Changed?.Invoke();
        Reroute();
    }

    private void OnMapChanged()
    {
        PushTargets();
        Reroute();
        Changed?.Invoke();
        // The "no station" notice depends on the map; notices are raised on the UI thread.
        Dispatcher.UIThread.Post(() => NoticeChanged?.Invoke());
    }

    private void OnMixesChanged()
    {
        Map.ForgetMissingMixes([.. Mixes.Select(m => m.Id)]);
        MixesChanged?.Invoke();
    }

    // Play what the tuned station is assigned (the player when the game isn't connected).
    private void Reroute() => _host?.Playback.Route(Map.RouteFor(_currentStation));

    private void PushTargets() => _ipc.SendTargetStations(Map.TargetStations);

    // -- Volume / now playing --

    bool IGameVolume.SetMasterVolume(float gain) => _ipc.SendMasterVolume(gain);

    bool IGameVolume.SetDuckGain(float gain) => _ipc.SendGain(gain);

    bool INowPlayingDisplay.Show(Track track) => _ipc.SendTrack(track);

    bool INowPlayingDisplay.Clear() => _ipc.SendClearTrack();

    private void SetState(ConnectionState state)
    {
        if (Interlocked.Exchange(ref _state, (int)state) == (int)state) return;
        if (state == ConnectionState.Connected)
        {
            // The DLL starts out replacing any station; tell it ours. It reports the tuned
            // station right after connecting, which routes playback.
            PushTargets();
        }
        else if (_currentStation != null)
        {
            // Lost the game: forget the station and fall back to plain player playback.
            _currentStation = null;
            Changed?.Invoke();
            Reroute();
        }
        StateChanged?.Invoke(state);
    }

    // Same relative order the app used before the extraction: telemetry, then the control pipe,
    // then the audio pipe. The host disposes this after the playback runner, so nothing is still
    // writing PCM when the pipe closes.
    public async ValueTask DisposeAsync()
    {
        if (_host != null) _host.Playback.MixesChanged -= OnMixesChanged;
        _telemetry.Dispose();
        await _ipc.DisposeAsync().ConfigureAwait(false);
        await _pcm.DisposeAsync().ConfigureAwait(false);
    }
}
