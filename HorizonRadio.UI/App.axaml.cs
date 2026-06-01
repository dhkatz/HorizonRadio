using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Avalonia.Threading;
using HorizonRadio.Core.Events;
using HorizonRadio.Core.Ipc;
using HorizonRadio.Core.Metadata;
using HorizonRadio.Core.Models;
using HorizonRadio.Core.Sources;
using HorizonRadio.Core.Sources.Config;
using HorizonRadio.UI.Tools;
using HorizonRadio.UI.ViewModels;
using HorizonRadio.UI.Views;

namespace HorizonRadio.UI;

[System.Diagnostics.CodeAnalysis.SuppressMessage(
    "Design",
    "CA1001:Types that own disposable fields should be disposable",
    Justification = "Avalonia owns the Application instance; the desktop shutdown hook disposes owned services.")]
public partial class App : Application
{
    private IpcClient? _ipc;
    private PcmPipeClient? _pcm;
    private SourceRunner? _runner;
    private SourceConfigStore? _store;
    private EnrichmentService? _enricher;
    private MetadataConfigStore? _metaStore;
    private EventActionExecutor? _eventExecutor;
    private ForzaTelemetryListener? _telemetry;

    public override void Initialize()
    {
        AvaloniaXamlLoader.Load(this);
    }

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            _store = SourceConfigStore.LoadFromDisk();

            _pcm = new PcmPipeClient();
            _pcm.Start();
            var sink = new PcmPipeSink(_pcm);

            _runner = new SourceRunner(sink) { Shuffle = _store.Shuffle };

            _metaStore = MetadataConfigStore.LoadFromDisk();
            var cache = new MetadataCache();
            _enricher = new EnrichmentService(_runner, provider: null);
            var metaVm = new MetadataViewModel(_metaStore, cache, _enricher);

            var toolRegistry = new ToolRegistry();
            var installers = new IToolInstaller[]
            {
                new YtDlpInstaller(),
                new FfmpegInstaller(),
            };

            // IPC client doubles as a game-event source (the DLL's memory
            // poller); the telemetry listener is a second source. The
            // executor runs the user's configured action for each event.
            _ipc = new IpcClient();
            var eventRules = EventRuleStore.LoadFromDisk();
            _telemetry = new ForzaTelemetryListener();
            _eventExecutor = new EventActionExecutor(
                new IGameEventSource[] { _ipc, _telemetry },
                _runner, _store, eventRules, _ipc.SendGain);
            var eventsVm = new EventsViewModel(eventRules, _eventExecutor, ForzaTelemetryListener.DefaultPort);

            var vm = new MainWindowViewModel(_runner, _store, metaVm, toolRegistry, installers, eventsVm);
            desktop.MainWindow = new MainWindow { DataContext = vm };

            // Station targeting: push the chosen station to the DLL on change
            // and re-send it whenever the DLL (re)connects, so it knows which
            // station to replace.
            vm.Sources.TargetStationChanged += s => _ipc?.SendTargetStation(StationCatalog.ToWire(s));

            _ipc.Connected += () =>
            {
                Dispatcher.UIThread.Post(() => vm.SetConnection(ConnectionState.Connected));
                _ipc?.SendTargetStation(StationCatalog.ToWire(vm.Sources.SelectedStation));
            };
            _ipc.Disconnected += () => Dispatcher.UIThread.Post(() => vm.SetConnection(ConnectionState.Disconnected));
            _ipc.StatsUpdated += s => Dispatcher.UIThread.Post(() => vm.Stats.Apply(s));
            _ipc.Start();
            _telemetry.Start();

            _runner.TrackChanged += t =>
            {
                Dispatcher.UIThread.Post(() => vm.NowPlaying.Apply(t));
                _ipc?.SendTrack(t);
            };

            _enricher.TrackEnriched += t =>
            {
                Dispatcher.UIThread.Post(() => vm.NowPlaying.Apply(t));
                _ipc?.SendTrack(t);
            };

            desktop.ShutdownRequested += async (_, _) =>
            {
                _eventExecutor?.Dispose();
                _telemetry?.Dispose();
                if (_enricher != null) await _enricher.DisposeAsync();
                if (_runner != null) await _runner.DisposeAsync();
                if (_ipc != null) await _ipc.DisposeAsync();
                if (_pcm != null) await _pcm.DisposeAsync();
            };

            vm.SetConnection(ConnectionState.Connecting);
        }

        base.OnFrameworkInitializationCompleted();
    }
}
