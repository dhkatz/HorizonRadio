using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Avalonia.Threading;
using HorizonRadio.Core.Audio;
using HorizonRadio.Core.Events;
using HorizonRadio.Core.Input;
using HorizonRadio.Core.Ipc;
using HorizonRadio.Core.Metadata;
using HorizonRadio.Core.Models;
using HorizonRadio.Core.Sources;
using HorizonRadio.Core.Sources.Config;
using HorizonRadio.Core.Sources.Mixes;
using HorizonRadio.UI.Tools;
using HorizonRadio.UI.ViewModels;
using HorizonRadio.UI.Views;
using ShadUI;

namespace HorizonRadio.UI;

[System.Diagnostics.CodeAnalysis.SuppressMessage(
    "Design",
    "CA1001:Types that own disposable fields should be disposable",
    Justification = "Avalonia owns the Application instance; the desktop shutdown hook disposes owned services.")]
public partial class App : Application
{
    private IpcClient? _ipc;
    private PcmPipeClient? _pcm;
    private PreviewController? _preview;
    private SourceRunner? _runner;
    private SourceConfigStore? _store;
    private EnrichmentService? _enricher;
    private MetadataConfigStore? _metaStore;
    private EventActionExecutor? _eventExecutor;
    private ForzaTelemetryListener? _telemetry;
    private InputBindingService? _inputService;

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
            // Tee the pipeline so the active source can play to the game pipe
            // and (optionally) to local speakers for in-app test playback.
            var tee = new TeePcmSink(new PcmPipeSink(_pcm));
            _preview = new PreviewController(tee, _store);

            _runner = new SourceRunner(tee) { Shuffle = _store.Shuffle };

            _metaStore = MetadataConfigStore.LoadFromDisk();
            var cache = new MetadataCache();
            _enricher = new EnrichmentService(_runner, provider: null);
            var metaVm = new MetadataViewModel(_metaStore, cache, _enricher);

            var toolRegistry = new ToolRegistry();
            var installers = ToolInstallers.CreateAll();

            // IPC client doubles as a game-event source (the DLL's memory
            // poller); the telemetry listener is a second source. The
            // executor runs the user's configured action for each event.
            _ipc = new IpcClient();
            var eventRules = EventRuleStore.LoadFromDisk();
            _telemetry = new ForzaTelemetryListener();

            // Saved mixes + the single switcher all launches route through (owns
            // the "current mix" notion for Next/Previous). One-time migrate any
            // legacy profiles.json into one-entry mixes on first run.
            var mixStore = MixStore.LoadFromDisk();
            MixMigration.MaybeMigrate(mixStore);
            var mixSwitcher = new MixSwitcher(mixStore, _store, _runner);

            // One dispatcher turns an EventAction into a transport/source/mix/
            // volume call; both the Events tab (game events) and the Controls tab
            // (input bindings) feed it, so they share capability checks.
            var dispatcher = new ActionDispatcher(_runner, _store, mixSwitcher, _ipc.SendGain);
            _eventExecutor = new EventActionExecutor(
                new IGameEventSource[] { _ipc, _telemetry }, eventRules, dispatcher);
            var eventsVm = new EventsViewModel(eventRules, _eventExecutor, ForzaTelemetryListener.DefaultPort);

            // Controls: global keyboard/mouse (SharpHook) + controllers (SDL),
            // mapped to the same actions through the shared dispatcher.
            var controlsStore = InputBindingStore.LoadFromDisk();
            _inputService = new InputBindingService(
                new IInputBackend[] { new SharpHookBackend(), new SdlInputBackend() },
                controlsStore, dispatcher);
            var controlsVm = new ControlsViewModel(controlsStore, _inputService, mixStore);

            var toasts = new ShadUI.ToastManager();
            var vm = new MainWindowViewModel(_runner, _store, mixStore, mixSwitcher, metaVm, toolRegistry, installers, eventsVm, controlsVm, _preview, toasts);
            desktop.MainWindow = new MainWindow { DataContext = vm };

            // Station targeting: push the chosen station to the DLL on change
            // and re-send it whenever the DLL (re)connects, so it knows which
            // station to replace.
            vm.NowPlaying.Station.TargetStationChanged += s => _ipc?.SendTargetStation(StationCatalog.ToWire(s));

            // A mix can override the target station: push its effective station on
            // switch (its own, else the global default), and revert to the global
            // default when a non-mix (self-driven) source starts directly.
            mixSwitcher.Switched += mix =>
                _ipc?.SendTargetStation(
                    StationCatalog.ToWire(mix.EffectiveStation(vm.NowPlaying.Station.SelectedStation)));
            _runner.ActiveSourceChanged += factory =>
            {
                if (factory != null)
                    _ipc?.SendTargetStation(StationCatalog.ToWire(vm.NowPlaying.Station.SelectedStation));
            };

            _ipc.Connected += () =>
            {
                Dispatcher.UIThread.Post(() => vm.SetConnection(ConnectionState.Connected));
                _ipc?.SendTargetStation(StationCatalog.ToWire(vm.NowPlaying.Station.SelectedStation));
            };
            _ipc.Disconnected += () => Dispatcher.UIThread.Post(() => vm.SetConnection(ConnectionState.Disconnected));
            _ipc.StatsUpdated += s => Dispatcher.UIThread.Post(() => vm.Stats.Apply(s));
            _ipc.Start();
            _telemetry.Start();
            _inputService.Start();

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
                _inputService?.Dispose();
                mixSwitcher.Dispose();
                _preview?.Dispose();
                _telemetry?.Dispose();
                if (_enricher != null) await _enricher.DisposeAsync();
                if (_runner != null) await _runner.DisposeAsync();
                if (_ipc != null) await _ipc.DisposeAsync();
                if (_pcm != null) await _pcm.DisposeAsync();
            };

            vm.SetConnection(ConnectionState.Connecting);

            // Background provisioning-freshness check: surface stale tools
            // on the sidebar badge and via a one-time launch toast. Runs on
            // the UI thread (CheckFreshnessAsync awaits the network off it)
            // and is failure-silent — offline resolves to Unknown, no toast.
            CheckToolFreshnessAsync(vm);

            // App self-update check: surface a newer build via the About
            // footer badge + a one-time launch toast. Dev builds no-op.
            CheckAppUpdateAsync(vm);
        }

        base.OnFrameworkInitializationCompleted();
    }

    private static async void CheckAppUpdateAsync(MainWindowViewModel vm)
    {
        try
        {
            if (!await vm.About.CheckForUpdatesAsync()) return;

            vm.ToastManager.CreateToast("Update available")
                .WithContent("A newer build of Horizon Radio is available. Open About to update.")
                .WithDelay(8)
                .DismissOnClick()
                .ShowInfo();
        }
        catch
        {
            // Update checking is best-effort; never disrupt startup.
        }
    }

    private static async void CheckToolFreshnessAsync(MainWindowViewModel vm)
    {
        try
        {
            var n = await vm.ToolsTab.CheckFreshnessAsync();
            if (n <= 0) return;

            vm.ToastManager.CreateToast("Tool updates available")
                .WithContent($"{n} installed tool{(n == 1 ? "" : "s")} " +
                             $"{(n == 1 ? "has" : "have")} a newer build. " +
                             "Open the Tools tab to update.")
                .WithDelay(8)
                .DismissOnClick()
                .ShowInfo();
        }
        catch
        {
            // Freshness is best-effort; never let it disrupt startup.
        }
    }
}
