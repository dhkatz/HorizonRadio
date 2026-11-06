using System.ComponentModel;
using Avalonia.Threading;
using HorizonRadio.Core.Events;
using HorizonRadio.Core.Games;
using HorizonRadio.Core.Ipc;
using HorizonRadio.Core.Models;
using HorizonRadio.Core.ModInstall;
using HorizonRadio.Core.Sources;
using HorizonRadio.Game.ForzaHorizon6.ViewModels;

namespace HorizonRadio.Game.ForzaHorizon6;

/// <summary>
/// The FH6 integration: the injected DLL's two named pipes (JSON control channel + PCM audio)
/// plus the optional Forza "Data Out" UDP telemetry feed as a second event source.
/// </summary>
public sealed class Fh6GameIntegration : IGameIntegration, IStationTargeting, IGameVolume, INowPlayingDisplay
{
    private readonly IpcClient _ipc = new();
    private readonly PcmPipeClient _pcm = new();
    private readonly ForzaTelemetryListener _telemetry = new();
    private volatile int _state = (int)ConnectionState.Disconnected;
    private IReadOnlyList<GamePage>? _pages;
    private ModManagerViewModel? _modManager;

    public Fh6GameIntegration(string displayName)
    {
        DisplayName = displayName;
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

    // The DLL's wire value for "any station" is "*"; IpcClient maps null/empty to it.
    bool IStationTargeting.SetTarget(string? station) => _ipc.SendTargetStation(station);

    bool IGameVolume.SetMasterVolume(float gain) => _ipc.SendMasterVolume(gain);

    bool IGameVolume.SetDuckGain(float gain) => _ipc.SendGain(gain);

    bool INowPlayingDisplay.Show(Track track) => _ipc.SendTrack(track);

    private void SetState(ConnectionState state)
    {
        if (Interlocked.Exchange(ref _state, (int)state) != (int)state)
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
