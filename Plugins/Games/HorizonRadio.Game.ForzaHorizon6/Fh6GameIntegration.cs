using HorizonRadio.Core.Events;
using HorizonRadio.Core.Games;
using HorizonRadio.Core.Ipc;
using HorizonRadio.Core.Models;
using HorizonRadio.Core.Sources;

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

    /// <summary>Per-tick audio-bridge stats from the DLL (FH6-specific; feeds the Stats page).
    /// Raised on the IPC reader thread.</summary>
    public event Action<BridgeStats>? StatsUpdated
    {
        add => _ipc.StatsUpdated += value;
        remove => _ipc.StatsUpdated -= value;
    }

    /// <summary>The UDP port the Forza Data Out telemetry listener binds (shown in the Events tab
    /// as the address to configure in-game).</summary>
    public static int TelemetryPort => ForzaTelemetryListener.DefaultPort;

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
