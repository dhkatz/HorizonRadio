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
/// It owns station targeting end to end: the default station (persisted in the plugin's
/// settings), the active mix's override (from the mix's FH6 settings), the effective station, and
/// re-sending it to the DLL whenever it changes or the DLL (re)connects.
/// </summary>
public sealed class Fh6GameIntegration
    : IGameIntegration, IStationTargeting, IGameMixSettings, IGameVolume, INowPlayingDisplay
{
    private readonly IpcClient _ipc = new();
    private readonly PcmPipeClient _pcm = new();
    private readonly ForzaTelemetryListener _telemetry = new();
    private volatile int _state = (int)ConnectionState.Disconnected;
    private IReadOnlyList<GamePage>? _pages;
    private ModManagerViewModel? _modManager;

    // Station state. _defaultStation persists in plugin settings; _mixStation is the active mix's
    // override. null means "whatever station is active" for both.
    private const string DefaultStationKey = "station";
    private readonly IPluginSettings? _settings;
    private readonly Lock _stationLock = new();
    private string? _defaultStation;
    private string? _mixStation;

    /// <param name="displayName">The game's name.</param>
    /// <param name="settings">Where the default station persists; <see langword="null"/> keeps it
    /// in memory only (tests).</param>
    public Fh6GameIntegration(string displayName, IPluginSettings? settings = null)
    {
        DisplayName = displayName;
        _settings = settings;
        _defaultStation = ValidStation(settings?.GetValue(DefaultStationKey));
        Output = new PcmPipeSink(_pcm);
        EventSources = [_ipc, _telemetry];
        _ipc.Connected += () => SetState(ConnectionState.Connected);
        _ipc.Disconnected += () => SetState(ConnectionState.Disconnected);
    }

    public string DisplayName { get; }

    public IPcmSink Output { get; }

    public IReadOnlyList<IGameEventSource> EventSources { get; }

    public ConnectionState State => (ConnectionState)_state;

    public event Action<ConnectionState>? StateChanged;

    public string EventsHint =>
        $"For richer events, enable Forza “Data Out” in-game and point it at 127.0.0.1:{ForzaTelemetryListener.DefaultPort}.";

    public event Action? NoticeChanged;

    /// <summary>The Mods page (install / update the version.dll mod) and the Stats page (live
    /// audio-bridge telemetry). Built lazily: the mod manager scans for game installs and hashes
    /// the bundled DLL, and both need the UI thread.</summary>
    public IReadOnlyList<GamePage> Pages => _pages ??= BuildPages();

    public GameNotice? Notice => _modManager?.Status switch
    {
        InstallationStatus.InstalledOther => new GameNotice(
            "Mod Update Available", "A different version is installed in your game folder.", ModsPageId),
        InstallationStatus.NotInstalled => new GameNotice(
            "Mod Not Installed", "Horizon Radio isn't set up in your game yet.", ModsPageId),
        _ => null,
    };

    private const string ModsPageId = "mods";

    private IReadOnlyList<GamePage> BuildPages()
    {
        _modManager = new ModManagerViewModel();
        _modManager.PropertyChanged += OnModManagerChanged;

        var stats = new StatsViewModel();
        // IPC events arrive on the pipe reader thread; the page binds on the UI thread.
        _ipc.StatsUpdated += s => Dispatcher.UIThread.Post(() => stats.Apply(s));
        _ipc.SourceChanged += s => Dispatcher.UIThread.Post(() => stats.SetSource(s.DisplayName));
        StateChanged += state => Dispatcher.UIThread.Post(() =>
            stats.SetConnectionState(state == ConnectionState.Connected));

        return
        [
            new GamePage(ModsPageId, "Mods", "PuzzleOutline", _modManager),
            new GamePage("stats", "Stats", "ChartBar", stats),
        ];
    }

    private void OnModManagerChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(ModManagerViewModel.Status))
            NoticeChanged?.Invoke();
    }

    public IStationTargeting Stations => this;

    public IGameMixSettings MixSettings => this;

    public IGameVolume Volume => this;

    public INowPlayingDisplay NowPlaying => this;

    public void Start()
    {
        SetState(ConnectionState.Connecting);
        _pcm.Start();
        _ipc.Start();
        _telemetry.Start();
    }

    IReadOnlyList<string> IStationTargeting.Stations => StationCatalog.Names;

    public event Action? Changed;

    public string? DefaultStation
    {
        get { lock (_stationLock) return _defaultStation; }
        set
        {
            var station = ValidStation(value);
            UpdateStations(() =>
            {
                if (_defaultStation == station) return false;
                _defaultStation = station;
                return true;
            });
            _settings?.SetValue(DefaultStationKey, station);
        }
    }

    public string? EffectiveStation
    {
        get { lock (_stationLock) return _mixStation ?? _defaultStation; }
    }

    void IGameMixSettings.ApplyMix(IReadOnlyDictionary<string, string>? settings)
    {
        var station = ValidStation(settings?.GetValueOrDefault(MixSettingsViewModel.StationKey));
        UpdateStations(() =>
        {
            if (_mixStation == station) return false;
            _mixStation = station;
            return true;
        });
    }

    IGameMixEditor IGameMixSettings.CreateEditor(IReadOnlyDictionary<string, string>? settings) =>
        new MixSettingsViewModel(StationCatalog.Names, settings);

    string? IGameMixSettings.Describe(IReadOnlyDictionary<string, string>? settings) =>
        ValidStation(settings?.GetValueOrDefault(MixSettingsViewModel.StationKey)) is { } station
            ? $"Station: {station}"
            : null;

    // Apply a station-state change under the lock; if it changed anything, notify and push the
    // (possibly new) effective station to the DLL. Pushing is best-effort: when the DLL isn't
    // connected it's re-sent on connect (see SetState).
    private void UpdateStations(Func<bool> change)
    {
        lock (_stationLock)
        {
            if (!change()) return;
        }
        Changed?.Invoke();
        PushStation();
    }

    // The DLL's wire value for "any station" is "*"; IpcClient maps null to it.
    private void PushStation() => _ipc.SendTargetStation(EffectiveStation);

    // Only names from the station list are valid targets; anything else (stale config, a station
    // renamed by a game update) means "whatever is active".
    private static string? ValidStation(string? station) =>
        station != null && StationCatalog.Names.Contains(station) ? station : null;

    bool IGameVolume.SetMasterVolume(float gain) => _ipc.SendMasterVolume(gain);

    bool IGameVolume.SetDuckGain(float gain) => _ipc.SendGain(gain);

    bool INowPlayingDisplay.Show(Track track) => _ipc.SendTrack(track);

    private void SetState(ConnectionState state)
    {
        if (Interlocked.Exchange(ref _state, (int)state) == (int)state) return;
        // The DLL starts with no target (it replaces any station), so re-assert ours on every connect.
        if (state == ConnectionState.Connected) PushStation();
        StateChanged?.Invoke(state);
    }

    // Same relative order the app used before the extraction: telemetry, then the control pipe,
    // then the audio pipe. The host disposes this after the playback runner, so nothing is still
    // writing PCM when the pipe closes.
    public async ValueTask DisposeAsync()
    {
        _telemetry.Dispose();
        await _ipc.DisposeAsync().ConfigureAwait(false);
        await _pcm.DisposeAsync().ConfigureAwait(false);
    }
}
