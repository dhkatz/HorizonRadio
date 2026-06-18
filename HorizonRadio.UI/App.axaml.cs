using System.IO;
using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Avalonia.Threading;
using HorizonRadio.Core;
using HorizonRadio.Core.Audio;
using HorizonRadio.Core.Events;
using HorizonRadio.Core.History;
using HorizonRadio.Core.Input;
using HorizonRadio.Core.Ipc;
using HorizonRadio.Core.Metadata;
using HorizonRadio.Core.Models;
using HorizonRadio.Core.Sources;
using HorizonRadio.Core.Sources.Config;
using HorizonRadio.Core.Sources.Mixes;
using HorizonRadio.Core.Sources.Queue;
using HorizonRadio.Core.Sources.Spotify;
using HorizonRadio.Core.Sources.YouTube;
using HorizonRadio.Core.Tools;
using HorizonRadio.TitleModel;
using HorizonRadio.UI.Tools;
using HorizonRadio.UI.ViewModels;
using HorizonRadio.UI.Views;
using Microsoft.Extensions.DependencyInjection;
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
    private MetadataResolver? _metaResolver;
    private MetadataConfigStore? _metaStore;
    private ITitleExtractor? _titleExtractor;
    // Identity (path + last-write) of the installed title model the current extractor was built for,
    // so ReinitTitleModel rebuilds it when the file actually changes (reinstall/update) and skips
    // unrelated tool changes. Sentinel so the first call always initializes the runtime.
    private (string? Path, long WriteTicks) _titleModelStamp = ("\0uninitialized", -1);
    private EventActionExecutor? _eventExecutor;
    private ForzaTelemetryListener? _telemetry;
    private InputBindingService? _inputService;
    private SpotifyConnection? _spotifyConnection;
    private SpotifyPlaybackService? _spotifyPlayback;
    private PlayHistoryStore? _historyStore;
    private PlayHistoryService? _historyService;

    public override void Initialize()
    {
        AvaloniaXamlLoader.Load(this);
    }

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            // Re-arm opt-in metadata diagnostics if the user left them on (or HZN_META_TRACE is set)
            // before any source can emit, so the first song of the session is captured too.
            HorizonRadio.Core.Diagnostics.MetadataTrace.RestoreFromSettings();

            // Build the DI container for the Core engine's leaf services (the persisted config
            // stores + the metadata cache) and resolve them from it instead of hand-constructing
            // them — the first step of moving ownership/lifetime to DI. Built inside the desktop
            // branch so the XAML designer (which never enters here) is unaffected.
            var services = new ServiceCollection();
            services.AddHorizonCore();
            var provider = services.BuildServiceProvider();

            _store = provider.GetRequiredService<SourceConfigStore>();

            _pcm = new PcmPipeClient();
            _pcm.Start();
            // Tee the pipeline so the active source can play to the game pipe
            // and (optionally) to local speakers for in-app test playback.
            var tee = new TeePcmSink(new PcmPipeSink(_pcm));
            _preview = new PreviewController(tee, _store);

            _runner = new SourceRunner(tee) { Shuffle = _store.Shuffle };

            // Spotify: the account connection (PKCE, bring-your-own Client ID) and the
            // shared librespot playback service our engine drives via the Web API are
            // app singletons published for the (parameterless) content-source factory.
            // The service reads its librespot options fresh on each launch (via the
            // closure below) so installing librespot or editing the config mid-session
            // takes effect without an app restart.
            var spotifyFactory = (SpotifyContentSourceFactory)SourceCatalog.Find(SpotifyContentSourceFactory.SourceId)!;
            _spotifyConnection = new SpotifyConnection(
                new SpotifyAuthStore(),
                _store.Load(spotifyFactory.Id, spotifyFactory.Schema)
                    .GetString(SpotifyContentSourceFactory.KeyClientId) ?? "");
            _spotifyPlayback = new SpotifyPlaybackService(
                _spotifyConnection, () => LoadLibrespotOptions(_store!, spotifyFactory));
            SpotifyRuntime.Initialize(_spotifyConnection, _spotifyPlayback);

            // The driven service and the zero-config "Spotify Connect" receiver both
            // drive the "Horizon Radio" librespot device; when the receiver (id
            // "spotify") becomes the active source, release the driven service's
            // librespot so the two don't fight over the device/cache.
            _runner.ActiveSourceChanged += factory =>
            {
                if (factory?.Id == "spotify") _ = _spotifyPlayback.ReleaseAsync();
            };

            // YouTube search needs the configured yt-dlp path; publish a resolver that
            // reads it fresh from the persisted config (so installing yt-dlp via the
            // Tools tab mid-session takes effect without a restart), or null when it
            // isn't set/installed yet — which the search source treats as "no results".
            var youtubeFactory = (YouTubeSourceFactory)SourceCatalog.Find(YouTubeSourceFactory.SourceId)!;
            YouTubeRuntime.Initialize(() =>
            {
                var path = _store!.Load(youtubeFactory.Id, youtubeFactory.Schema)
                    .GetString(YouTubeSourceFactory.KeyYtDlp);
                return !string.IsNullOrWhiteSpace(path) && File.Exists(path) ? path : null;
            });

            _metaStore = provider.GetRequiredService<MetadataConfigStore>();
            var cache = provider.GetRequiredService<MetadataCache>();
            // The metadata pipeline: a shared resolver (source + ordered providers,
            // per-field policy) drives both play-time enrichment and list enrichment.
            _metaResolver = new MetadataResolver();
            var (metaContributors, metaPolicy) = MetadataCatalog.BuildPipeline(_metaStore, cache);
            _metaResolver.Configure(metaContributors, metaPolicy);
            _enricher = new EnrichmentService(_runner, _metaResolver);
            var metaVm = new MetadataViewModel(_metaStore, cache, _metaResolver);

            var toolRegistry = new ToolRegistry();
            var installers = ToolInstallers.CreateAll();

            // Optional local title-extraction model: published to the runtime holder so the
            // (parameterless) radio source can reach it. Constructed only when a model is actually
            // installed — the extractor loads lazily on first use, but constructing it eagerly
            // would have every shaky title spawn a no-op background task. Re-init on tool changes
            // so installing/uninstalling the model mid-session takes effect without a restart.
            void ReinitTitleModel()
            {
                var path = ToolResolver.Discover(ToolKind.TitleModel);
                long ticks = 0;
                if (!string.IsNullOrEmpty(path))
                    try { ticks = File.GetLastWriteTimeUtc(path).Ticks; } catch { /* unreadable → 0 */ }

                // Skip unrelated tool changes (e.g. installing ffmpeg) — only rebuild when the model
                // file's identity changed. A genuine reinstall/update changes the write time, so we
                // build a FRESH extractor rather than reusing the cached one (which would keep serving
                // the previously-loaded weights until restart).
                var stamp = (path, ticks);
                if (stamp == _titleModelStamp) return;
                _titleModelStamp = stamp;

                var old = _titleExtractor;
                _titleExtractor = string.IsNullOrEmpty(path)
                    ? null
                    : new LlamaTitleExtractor(() => ToolResolver.Discover(ToolKind.TitleModel));
                TitleExtractorRuntime.Initialize(_titleExtractor, _metaStore!.TitleModelMode);
                // Dispose the superseded extractor; DisposeAsync waits for any in-flight inference
                // before freeing the native model, so this is safe to fire-and-forget.
                if (old != null) _ = old.DisposeAsync().AsTask();
            }
            ReinitTitleModel();
            toolRegistry.Changed += ReinitTitleModel;

            // IPC client doubles as a game-event source (the DLL's memory
            // poller); the telemetry listener is a second source. The
            // executor runs the user's configured action for each event.
            _ipc = new IpcClient();
            var eventRules = provider.GetRequiredService<EventRuleStore>();
            _telemetry = new ForzaTelemetryListener();

            // Saved mixes + the single switcher all launches route through (owns
            // the "current mix" notion for Next/Previous). One-time migrate any
            // legacy profiles.json into one-entry mixes on first run.
            var mixStore = provider.GetRequiredService<MixStore>();
            MixMigration.MaybeMigrate(mixStore);

            // The global queue owns playback now: one engine plays straight down the
            // queue (explicit one-offs first, then the active mix as an infinite
            // tail). The switcher sets a mix as that tail; quick-play appends one-offs.
            var contentResolver = new MixContentResolver(_store);
            var queuePlayback = new QueuePlayback(_runner, _store, contentResolver);
            var mixSwitcher = new MixSwitcher(mixStore, queuePlayback, _runner);

            // Play history: records every song the runner reports (deduped), tags freeform songs
            // it can't identify via the metadata pipeline, and persists (debounced) to history.json.
            _historyStore = provider.GetRequiredService<PlayHistoryStore>();
            _historyService = new PlayHistoryService(_historyStore, _runner);

            // One dispatcher turns an EventAction into a transport/source/mix/
            // volume call; both the Events tab (game events) and the Controls tab
            // (input bindings) feed it, so they share capability checks.
            var dispatcher = new ActionDispatcher(_runner, _store, mixSwitcher, _ipc.SendGain);
            _eventExecutor = new EventActionExecutor(
                new IGameEventSource[] { _ipc, _telemetry }, eventRules, dispatcher);
            var eventsVm = new EventsViewModel(eventRules, _eventExecutor, ForzaTelemetryListener.DefaultPort);

            // Controls: global keyboard/mouse (SharpHook) + controllers (SDL),
            // mapped to the same actions through the shared dispatcher.
            var controlsStore = provider.GetRequiredService<InputBindingStore>();
            _inputService = new InputBindingService(
                new IInputBackend[] { new SharpHookBackend(), new SdlInputBackend() },
                controlsStore, dispatcher);
            var controlsVm = new ControlsViewModel(controlsStore, _inputService, mixStore);

            var toasts = new ShadUI.ToastManager();

            // ShadUI maps dialog view models to views via its own registry (not
            // the app ViewLocator), so custom dialog content must be registered.
            var dialogManager = new ShadUI.DialogManager();
            dialogManager.Register<QuickPlayDialogView, QuickPlayDialogViewModel>();
            dialogManager.Register<QueueAddModeDialogView, QueueAddModeDialogViewModel>();

            var vm = new MainWindowViewModel(_runner, _store, mixStore, mixSwitcher, queuePlayback, _historyStore, _metaResolver, contentResolver, metaVm, toolRegistry, installers, eventsVm, controlsVm, _preview, toasts, dialogManager);
            desktop.MainWindow = new MainWindow { DataContext = vm };

            // Station targeting. "Which in-game station do we replace right now?"
            // is computed in ONE place: the active mix's override if a mix is
            // driving playback, else the global default. Every trigger — the
            // global picker changing, a mix switch, a self-driven source start,
            // and DLL (re)connect — pushes that same effective value, so none of
            // them can clobber an active mix's override or go stale on reconnect.
            string EffectiveStation()
            {
                var global = vm.NowPlaying.Station.SelectedStation;
                var mix = mixSwitcher.CurrentMixId is { } id ? mixStore.Get(id) : null;
                return mix?.EffectiveStation(global) ?? global;
            }

            void PushStation() => _ipc?.SendTargetStation(StationCatalog.ToWire(EffectiveStation()));

            vm.NowPlaying.Station.TargetStationChanged += _ => PushStation();
            mixSwitcher.Switched += _ => PushStation();
            _runner.ActiveSourceChanged += _ => PushStation();

            // The master volume slider doubles as an in-game pre-amp: push its
            // tapered gain to the bridge on every change (live feedback while
            // dragging is intentional), and re-assert on (re)connect since the
            // DLL resets to its conservative default. Skip resends of an
            // unchanged value so a jiggle within one gain step — or a reconnect
            // at the same level — doesn't spam the pipe; `force` overrides the
            // dedup on connect, where the DLL genuinely needs the value again.
            var lastSentGain = float.NaN;
            void PushGain(bool force = false)
            {
                var gain = VolumeTaper.ToGain(vm.NowPlaying.PreviewVolume);
                if (!force && gain.Equals(lastSentGain)) return;
                if (_ipc?.SendMasterVolume(gain) == true) lastSentGain = gain;
            }

            vm.NowPlaying.MasterVolumeChanged += _ => PushGain();

            _ipc.Connected += () =>
            {
                Dispatcher.UIThread.Post(() => vm.SetConnection(ConnectionState.Connected));
                PushStation();
                PushGain(force: true);
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
                _historyService?.Dispose(); // unsubscribes from the runner + flushes the final save
                mixSwitcher.Dispose();
                queuePlayback.Dispose();
                _preview?.Dispose();
                _telemetry?.Dispose();
                if (_enricher != null) await _enricher.DisposeAsync();
                if (_metaResolver != null) await _metaResolver.DisposeAsync();
                if (_runner != null) await _runner.DisposeAsync();
                if (_spotifyPlayback != null) await _spotifyPlayback.DisposeAsync();
                if (_spotifyConnection != null) await _spotifyConnection.DisposeAsync();
                if (_ipc != null) await _ipc.DisposeAsync();
                if (_pcm != null) await _pcm.DisposeAsync();
                // Dispose the container last — it owns the leaf singletons (stores + cache).
                await provider.DisposeAsync();
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

    private static string Or(string? value, string fallback) =>
        string.IsNullOrWhiteSpace(value) ? fallback : value;

    // Read the driven Spotify source's librespot options fresh from the persisted
    // config (called on every librespot launch). Falls back to a live exe discovery
    // when the stored path is empty — e.g. librespot was installed via the Tools tab
    // after the schema default was first captured.
    private static LibrespotOptions LoadLibrespotOptions(SourceConfigStore store, SpotifyContentSourceFactory factory)
    {
        var cfg = store.Load(factory.Id, factory.Schema);
        return new LibrespotOptions
        {
            ExecutablePath = Or(cfg.GetString(SpotifyContentSourceFactory.KeyExecutable), Librespot.DiscoverExe() ?? ""),
            DeviceName = Or(cfg.GetString(SpotifyContentSourceFactory.KeyDeviceName), Librespot.DefaultDeviceName),
            CacheDirectory = Or(cfg.GetString(SpotifyContentSourceFactory.KeyCacheDir), Librespot.DefaultCacheDir),
            Bitrate = cfg.GetString(SpotifyContentSourceFactory.KeyBitrate) ?? "auto",
            EnableVolumeNormalisation = cfg.GetBool(SpotifyContentSourceFactory.KeyNormalise, true),
        };
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
