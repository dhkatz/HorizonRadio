using System;
using System.IO;
using System.Linq;
using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Avalonia.Threading;
using HorizonRadio.Core;
using HorizonRadio.Core.Audio;
using HorizonRadio.Core.Events;
using HorizonRadio.Core.Fingerprints;
using HorizonRadio.Core.Games;
using HorizonRadio.Core.History;
using HorizonRadio.Core.Input;
using HorizonRadio.Core.Lyrics;
using HorizonRadio.Core.Metadata;
using HorizonRadio.Core.Metadata.Apple;
using HorizonRadio.Core.Metadata.MusicBrainz;
using HorizonRadio.Core.Metadata.Spotify;
using HorizonRadio.Core.Metadata.VocaDb;
using HorizonRadio.Core.Models;
using HorizonRadio.Core.Playback;
using HorizonRadio.Core.Plugins;
using HorizonRadio.Core.Sources;
using HorizonRadio.Core.Sources.Config;
using HorizonRadio.Core.Sources.Local;
using HorizonRadio.Core.Sources.Mixes;
using HorizonRadio.Core.Sources.Queue;
using HorizonRadio.Core.Sources.Radio;
using HorizonRadio.Core.Sources.Spotify;
using HorizonRadio.Core.Sources.Test;
using HorizonRadio.Core.Sources.YouTube;
using HorizonRadio.Core.Tools;
using HorizonRadio.Plugins.Abstractions;
using HorizonRadio.TitleModel;
using HorizonRadio.Tools.Librespot;
using HorizonRadio.UI.Services;
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
    private IGameIntegration? _game;
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

            // Discover the source plugins by scanning the loaded plugin assemblies (rather than naming
            // each here) before anything resolves a source from the catalog — the seam the install
            // model rests on. SortOrder gives the picker its display order. (The tool catalog is
            // already seeded in Program.BuildAvaloniaApp, before any source ctor probes it.)
            SourceCatalog.Initialize(PluginDiscovery.DiscoverPlugins<ISourcePlugin>());

            // Build the DI container and resolve the safe-to-own services from it instead of
            // hand-constructing them — moving their ownership/lifetime to DI. AddHorizonCore wires
            // Core's services (Core owns how to build them); the two UI host services are registered
            // here directly since this is their only composition root. Built inside the desktop branch
            // so the XAML designer (which never enters here) is unaffected.
            var services = new ServiceCollection();
            services.AddHorizonCore();
            services.AddSingleton<ToolRegistry>();
            services.AddSingleton<IPluginContext>(sp => new HostPluginContext(sp.GetRequiredService<MetadataCache>(), sp.GetRequiredService<PluginSettingsStore>()));
            var provider = services.BuildServiceProvider();

            _store = provider.GetRequiredService<SourceConfigStore>();

            // The active game: one runs at a time, chosen by the user (a game plugin, or "No Game"
            // for a plain player). It's wired into the audio pipeline, events, and pages at startup,
            // so switching games restarts the app (Settings → Game).
            var pluginContext = provider.GetRequiredService<IPluginContext>();
            var pluginSettings = provider.GetRequiredService<PluginSettingsStore>();
            var gamePlugins = PluginDiscovery.DiscoverPlugins<IGamePlugin>();
            // Before any game is built: its settings must already hold anything migrated into them.
            // Saving drops the legacy fields so the migration can't re-apply later.
            var migratedMixes = provider.GetRequiredService<MixStore>();
            var migrated = LegacySettingsMigration.MigrateStations(_store, migratedMixes, pluginSettings);
            if (migrated.ConfigChanged) _store.SaveToDisk();
            if (migrated.MixesChanged) migratedMixes.SaveToDisk();

            var gameIds = gamePlugins.Select(g => g.Id).ToList();
            var gameId = GameSelection.Resolve(_store.GameId, gameIds, [.. gameIds.Where(pluginSettings.HasSettings)]);
            if (gameId == null)
            {
                // First run (or the saved game was uninstalled): ask before building anything
                // game-related, then carry on with the choice.
                var firstRun = new FirstRunViewModel(gamePlugins, _store);
                var picker = new FirstRunWindow { DataContext = firstRun };
                firstRun.Chosen += id =>
                {
                    Compose(id, showNow: true);
                    picker.Close();
                };
                desktop.MainWindow = picker;
            }
            else
            {
                // Remember a defaulted choice (an existing user's game) so it's explicit from now on.
                if (_store.GameId != gameId)
                {
                    _store.GameId = gameId;
                    _store.SaveToDisk();
                }
                Compose(gameId, showNow: false);
            }

            // Everything from here on depends on the game; runs now, or once the picker returns.
            void Compose(string chosenGameId, bool showNow)
            {
                _game = chosenGameId == NoGameIntegration.Id
                    ? new NoGameIntegration()
                    : gamePlugins.First(g => g.Id == chosenGameId).Create(pluginContext);

                // Tee the pipeline so the active source can play to the game
                // and (optionally) to local speakers for in-app test playback.
                var tee = new TeePcmSink(_game.Output);
                _preview = new PreviewController(tee, _store, a => Dispatcher.UIThread.Post(a));

                _runner = new SourceRunner(tee);

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
                string? YouTubeTool(string key)
                {
                    var path = _store!.Load(youtubeFactory.Id, youtubeFactory.Schema).GetString(key);
                    return !string.IsNullOrWhiteSpace(path) && File.Exists(path) ? path : null;
                }
                YouTubeRuntime.Initialize(
                    () => YouTubeTool(YouTubeSourceFactory.KeyYtDlp),
                    () => YouTubeTool(YouTubeSourceFactory.KeyFfmpeg));

                // Discover the metadata plugins by scanning the loaded plugin assemblies (rather than
                // naming each here) before the config store loads — it derives fresh-install defaults + the
                // "introduced" set from the catalog, so the catalog must be populated first. SortOrder gives
                // the provider list its display order.
                MetadataCatalog.Initialize(PluginDiscovery.DiscoverPlugins<IMetadataPlugin>());

                _metaStore = provider.GetRequiredService<MetadataConfigStore>();
                // pluginContext (resolved above for the game plugin) is also what metadata provider
                // factories receive: host services shared across pipeline builds.
                // The metadata pipeline: a shared resolver (source + ordered providers,
                // per-field policy) drives both play-time enrichment and list enrichment.
                _metaResolver = new MetadataResolver();
                var (metaContributors, metaPolicy) = MetadataCatalog.BuildPipeline(_metaStore, pluginContext);
                _metaResolver.Configure(metaContributors, metaPolicy);
                _enricher = new EnrichmentService(_runner, _metaResolver);
                var metaVm = new MetadataViewModel(_metaStore, pluginContext, _metaResolver);

                // Lyrics for the Now Playing page, from every lyrics plugin (Unison, LRCLIB), in
                // their SortOrder: the service prefers synced lyrics, then the earlier provider.
                var lyrics = new LyricsService([.. PluginDiscovery.DiscoverPlugins<ILyricsPlugin>()
                    .Select(p => p.CreateLyricsProvider(pluginContext))]);
                // Radio lyrics follow the song by lining the station's audio up against a recording
                // of it (the song's own video, fetched through a source that can, e.g. YouTube).
                var songSync = new LiveSongSync(() => SourceCatalog.All, new ReferenceStore(), _metaResolver);

                var toolRegistry = provider.GetRequiredService<ToolRegistry>();
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

                // The game integration supplies the event sources (for FH6: the DLL's memory poller
                // and the Data Out telemetry listener); the executor runs the user's configured action
                // for each event.
                var eventRules = provider.GetRequiredService<EventRuleStore>();

                // Saved mixes + the single switcher all launches route through (owns
                // the "current mix" notion for Next/Previous). One-time migrate any
                // legacy profiles.json into one-entry mixes on first run.
                var mixStore = provider.GetRequiredService<MixStore>();
                MixMigration.MaybeMigrate(mixStore);

                // The playback router is the one owner of the engine: everything that changes what
                // plays (the game tuning, the queue getting something to play, a source the user
                // starts or stops) goes through it, one step at a time. The game steers it between
                // channels: the app's player, one channel per mix, or nothing (for FH6: whatever the
                // tuned station is set to play). Without a game it stays on the player, which is
                // plain app playback. The player is the global queue: one engine plays straight
                // down it (explicit one-offs first, then the active mix as an infinite tail). The
                // switcher sets a mix as that tail; quick-play appends one-offs.
                var contentResolver = provider.GetRequiredService<MixContentResolver>();
                var router = new PlaybackRouter(_runner, _store, mixStore, contentResolver);
                var queuePlayback = router.Player;
                var mixSwitcher = new MixSwitcher(mixStore, queuePlayback, _runner);

                // Play history: records every song the runner reports (deduped), tags freeform songs
                // it can't identify via the metadata pipeline, and persists (debounced) to history.json.
                _historyStore = provider.GetRequiredService<PlayHistoryStore>();
                _historyService = new PlayHistoryService(_historyStore, _runner);

                // One dispatcher turns an EventAction into a transport/source/mix/
                // volume call; both the Events tab (game events) and the Controls tab
                // (input bindings) feed it, so they share capability checks.
                var dispatcher = new ActionDispatcher(router, _store, mixSwitcher,
                    _game.Volume is { } gameVolume ? gameVolume.SetDuckGain : null);
                _eventExecutor = new EventActionExecutor(_game.EventSources, eventRules, dispatcher);
                var eventsVm = new EventsViewModel(eventRules, _eventExecutor, _game.EventCatalog, _game.EventsHint);

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

                var gameSettings = new GameSettingsViewModel(gamePlugins, _store, chosenGameId);
                gameSettings.RestartRequested += () => Restart(desktop);
                var vm = new MainWindowViewModel(router, _store, mixStore, mixSwitcher, queuePlayback, _historyStore, _metaResolver, contentResolver, metaVm, toolRegistry, installers, eventsVm, controlsVm, _preview, _game, gameSettings, toasts, dialogManager, lyrics, songSync);
                var mainWindow = new MainWindow { DataContext = vm };
                desktop.MainWindow = mainWindow;
                // Composed after startup (the first-run picker), so show it ourselves.
                if (showNow) mainWindow.Show();

                // The master volume slider doubles as an in-game pre-amp: push its
                // tapered gain to the game on every change (live feedback while
                // dragging is intentional), and re-assert on (re)connect since the
                // game side resets to its conservative default. Skip resends of an
                // unchanged value so a jiggle within one gain step — or a reconnect
                // at the same level — doesn't spam the pipe; `force` overrides the
                // dedup on connect, where the game genuinely needs the value again.
                var lastSentGain = float.NaN;
                void PushGain(bool force = false)
                {
                    var gain = VolumeTaper.ToGain(vm.NowPlaying.PreviewVolume);
                    if (!force && gain.Equals(lastSentGain)) return;
                    if (_game.Volume?.SetMasterVolume(gain) == true) lastSentGain = gain;
                }

                vm.NowPlaying.MasterVolumeChanged += _ => PushGain();

                _game.StateChanged += state =>
                {
                    Dispatcher.UIThread.Post(() => vm.SetConnection(state));
                    // The game re-asserts its own station targets, duck gain, and now-playing title on
                    // connect; the master volume is ours to re-send.
                    if (state != ConnectionState.Connected) return;
                    PushGain(force: true);
                };
                _game.Start(new GameHost(router));
                _inputService.Start();

                _runner.TrackChanged += t =>
                {
                    Dispatcher.UIThread.Post(() => vm.NowPlaying.Apply(t));
                    _game.NowPlaying?.Show(t);
                };

                _enricher.TrackEnriched += t =>
                {
                    Dispatcher.UIThread.Post(() => vm.NowPlaying.Apply(t));
                    // A lookup can finish after its track was parked (the game routed elsewhere);
                    // don't put a title back up for a channel that's no longer playing.
                    if (_runner.ActiveSource != null) _game.NowPlaying?.Show(t);
                };

                // Nothing on the runner any more (stopped, or parked as the game switched channels):
                // the game shouldn't keep showing the last track. A channel that resumes re-announces
                // its track when it starts playing. (ActiveSourceChanged also fires with a null
                // factory when a channel starts, so check the runner itself.)
                _runner.ActiveSourceChanged += _ =>
                {
                    if (_runner.ActiveSource == null) _game.NowPlaying?.Clear();
                };

                desktop.ShutdownRequested += async (_, _) =>
                {
                    _eventExecutor?.Dispose();
                    _inputService?.Dispose();
                    _historyService?.Dispose(); // unsubscribes from the runner + flushes the final save
                    mixSwitcher.Dispose();
                    _preview?.Dispose();
                    if (_enricher != null) await _enricher.DisposeAsync();
                    await lyrics.DisposeAsync();
                    if (_metaResolver != null) await _metaResolver.DisposeAsync();
                    await router.DisposeAsync(); // stops the engine; disposes parked channels and the player
                    if (_runner != null) await _runner.DisposeAsync();
                    if (_spotifyPlayback != null) await _spotifyPlayback.DisposeAsync();
                    if (_spotifyConnection != null) await _spotifyConnection.DisposeAsync();
                    // After the runner, so nothing is still sending audio to the game.
                    if (_game != null) await _game.DisposeAsync();
                    // Dispose the container last — it owns the leaf singletons (stores + cache).
                    await provider.DisposeAsync();
                };

                // Background provisioning-freshness check: surface stale tools
                // on the sidebar badge and via a one-time launch toast. Runs on
                // the UI thread (CheckFreshnessAsync awaits the network off it)
                // and is failure-silent — offline resolves to Unknown, no toast.
                CheckToolFreshnessAsync(vm);

                // App self-update check: surface a newer build via the About
                // footer badge + a one-time launch toast. Dev builds no-op.
                CheckAppUpdateAsync(vm);
            }
        }

        base.OnFrameworkInitializationCompleted();
    }

    // Switching games rewires the audio pipeline, events, and pages, which are built once at
    // startup: relaunch with the new choice (already saved) and shut this instance down.
    private static void Restart(IClassicDesktopStyleApplicationLifetime desktop)
    {
        if (Environment.ProcessPath is { } exe)
        {
            try { System.Diagnostics.Process.Start(exe); }
            catch (Exception ex) { System.Diagnostics.Debug.WriteLine($"[hzn-app] restart failed: {ex.Message}"); }
        }
        desktop.Shutdown();
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
