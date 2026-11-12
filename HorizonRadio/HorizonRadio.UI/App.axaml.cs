using System;
using System.IO;
using System.Linq;
using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Avalonia.Threading;
using HorizonRadio.Core;
using HorizonRadio.Core.Audio;
using HorizonRadio.Core.Devices;
using HorizonRadio.Core.Events;
using HorizonRadio.Core.Fingerprints;
using HorizonRadio.Core.Games;
using HorizonRadio.Core.History;
using HorizonRadio.Core.Hosting;
using HorizonRadio.Core.Input;
using HorizonRadio.Core.Lyrics;
using HorizonRadio.Core.Metadata;
using HorizonRadio.Core.Playback;
using HorizonRadio.Core.Plugins;
using HorizonRadio.Core.Settings;
using HorizonRadio.Core.Sources;
using HorizonRadio.Core.Sources.Config;
using HorizonRadio.Core.Sources.Mixes;
using HorizonRadio.Core.Tools;
using HorizonRadio.Sdk;
using HorizonRadio.Sdk.Games;
using HorizonRadio.Sdk.Lyrics;
using HorizonRadio.Sdk.Metadata;
using HorizonRadio.Sdk.Models;
using HorizonRadio.Sdk.Tools;
using HorizonRadio.UI.Services;
using HorizonRadio.UI.ViewModels;
using HorizonRadio.UI.Views;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
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
    private EventActionExecutor? _eventExecutor;
    private InputBindingService? _inputService;
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
            // Everything the Console tab shows, and the app's own debug lines, also go to a file per
            // session (%LOCALAPPDATA%\HorizonRadio\logs), so a failure can be read afterwards.
            HorizonRadio.Core.Diagnostics.LogFile.Start();
            // Every setting the app keeps, in one file (settings.json); the first run on it brings
            // over the older per-feature files. Saved shortly after each change, and at exit.
            var settings = SettingsStore.Open();
            HorizonRadio.Core.Diagnostics.MetadataTrace.RestoreFromSettings(settings);

            // Build the DI container and resolve the safe-to-own services from it instead of
            // hand-constructing them — moving their ownership/lifetime to DI. AddHorizonCore wires
            // Core's services (Core owns how to build them); the UI host services are registered
            // here directly since this is their only composition root. Built inside the desktop branch
            // so the XAML designer (which never enters here) is unaffected.
            // ShadUI maps dialog view models to views via its own registry (not
            // the app ViewLocator), so custom dialog content must be registered.
            var dialogManager = new ShadUI.DialogManager();
            dialogManager.Register<QuickPlayDialogView, QuickPlayDialogViewModel>();
            dialogManager.Register<QueueAddModeDialogView, QueueAddModeDialogViewModel>();
            var confirmations = new DialogConfirmations(dialogManager);
            var toasts = new ShadUI.ToastManager();

            var services = new ServiceCollection();
            services.AddHorizonCore(settings);
            // The app asks with dialogs (Core's default goes ahead without asking).
            services.Replace(ServiceDescriptor.Singleton<IConfirmations>(confirmations));
            services.Replace(ServiceDescriptor.Singleton<INotifications>(new ToastNotifications(toasts)));
            // The plugins, found by scanning the loaded plugin assemblies rather than named here (the
            // seam the install model rests on), and their modules' services. Last, so a module can
            // replace or wrap any service above; the Console tab says when one does.
            services.AddPlugins(PluginDiscovery.PluginTypes(),
                line => HorizonRadio.Core.Diagnostics.ProcessConsole.Append("plugins", line));
            // ValidateOnBuild: a plugin asking for a service nobody registers fails here, at startup.
            var provider = services.BuildServiceProvider(new ServiceProviderOptions { ValidateOnBuild = true });

            // The tools and the sources, in display order (the container builds the tool catalog
            // first: sources look their tools up as they're built).
            var tools = provider.GetRequiredService<ToolCatalog>();
            var sources = provider.GetRequiredService<SourceCatalog>();

            _store = provider.GetRequiredService<SourceConfigStore>();

            // The active game: one runs at a time, chosen by the user (a game plugin, or "No Game"
            // for a plain player). It's wired into the audio pipeline, events, and pages at startup,
            // so switching games restarts the app (Settings → Game).
            var gamePlugins = provider.GetServices<IGamePlugin>().InDisplayOrder();
            // Before any game is built: its settings must already hold anything migrated into them.
            // Saving drops the legacy fields so the migration can't re-apply later.
            var migratedMixes = provider.GetRequiredService<MixStore>();
            var migrated = LegacySettingsMigration.MigrateStations(settings, migratedMixes);
            if (migrated.MixesChanged) migratedMixes.SaveToDisk();

            var gameIds = gamePlugins.Select(g => g.Id).ToList();
            var savedGameId = settings.Get(GameSelection.Chosen);
            var gameId = GameSelection.Resolve(savedGameId, gameIds, [.. gameIds.Where(id => PluginSettings.HasSettings(settings, id))]);
            if (gameId == null)
            {
                // First run (or the saved game was uninstalled): ask before building anything
                // game-related, then carry on with the choice.
                var firstRun = new FirstRunViewModel(gamePlugins, settings);
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
                if (savedGameId != gameId) settings.Set(GameSelection.Chosen, gameId);
                Compose(gameId, showNow: false);
            }

            // Everything from here on depends on the game; runs now, or once the picker returns.
            void Compose(string chosenGameId, bool showNow)
            {
                _game = chosenGameId == NoGameIntegration.Id
                    ? new NoGameIntegration()
                    : gamePlugins.First(g => g.Id == chosenGameId).Create();

                // Tee the pipeline so the active source can play to the game
                // and (optionally) to local speakers for in-app test playback.
                var tee = new TeePcmSink(_game.Output);
                _preview = new PreviewController(tee, settings);

                _runner = new SourceRunner(tee);


                _metaStore = provider.GetRequiredService<MetadataConfigStore>();
                // The metadata pipeline: a shared resolver (source + ordered providers,
                // per-field policy) drives both play-time enrichment and list enrichment.
                _metaResolver = new MetadataResolver();
                var (metaContributors, metaPolicy) = _metaStore.Catalog.BuildPipeline(_metaStore);
                _metaResolver.Configure(metaContributors, metaPolicy);
                _enricher = new EnrichmentService(_runner, _metaResolver);
                var metaVm = new MetadataViewModel(_metaStore, _metaResolver);

                // Lyrics for the Now Playing page, from every lyrics plugin (Unison, LRCLIB), in
                // their SortOrder: the service prefers synced lyrics, then the earlier provider.
                var lyrics = new LyricsService(provider.GetServices<ILyricsProvider>().InDisplayOrder());
                // Radio lyrics follow the song by lining the station's audio up against a recording
                // of it (the song's own video, fetched through a source that can, e.g. YouTube).
                var songSync = new LiveSongSync(() => sources.All, new ReferenceStore(), _metaResolver);

                var toolRegistry = provider.GetRequiredService<ToolRegistry>();
                var installers = tools.Plugins.Select(t => t.Installer).ToArray();

                // The game integration supplies the event sources (for FH6: the DLL's memory poller
                // and the Data Out telemetry listener); the executor runs the user's configured action
                // for each event.
                var eventRules = provider.GetRequiredService<EventRuleStore>();

                // Saved mixes + the single switcher all launches route through (owns
                // the "current mix" notion for Next/Previous). One-time migrate any
                // legacy profiles.json into one-entry mixes on first run.
                var mixStore = provider.GetRequiredService<MixStore>();
                MixMigration.MaybeMigrate(mixStore, sources);

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
                _historyService = new PlayHistoryService(_historyStore, _runner, sources);

                // One dispatcher turns an EventAction into a transport/source/mix/
                // volume call; both the Events tab (game events) and the Controls tab
                // (input bindings) feed it, so they share capability checks.
                var dispatcher = new ActionDispatcher(router, _store, mixSwitcher,
                    _game.Volume is { } gameVolume ? gameVolume.SetDuckGain : null);
                _eventExecutor = new EventActionExecutor(_game.EventSources, eventRules, dispatcher);
                var eventsVm = new EventsViewModel(eventRules, _eventExecutor, _game.EventCatalog, _game.EventsHint, sources);

                // Controls: global keyboard/mouse (SharpHook) + controllers (SDL),
                // mapped to the same actions through the shared dispatcher.
                var controlsStore = provider.GetRequiredService<InputBindingStore>();
                _inputService = new InputBindingService(
                    new IInputBackend[] { new SharpHookBackend(), new SdlInputBackend() },
                    controlsStore, dispatcher);
                var controlsVm = new ControlsViewModel(controlsStore, _inputService, mixStore);

                var gameSettings = new GameSettingsViewModel(gamePlugins, settings, chosenGameId);
                gameSettings.RestartRequested += () => Restart(desktop);
                // Your devices: paired PCs keep their synced settings, mixes and history in step over the peer
                // helper, which starts only once this PC is paired (or starts pairing). The mix store
                // belongs to the UI thread, so merges run there.
                var peers = new Core.Peers.SharedPeerNetwork(provider.GetRequiredService<Sdk.Peers.IPeerNetworkFactory>().StartAsync);
                var myDevices = new MyDevices(settings, peers.GetAsync,
                    data: [new MixSync(mixStore), new HistorySync(_historyStore)]);
                _ = myDevices.StartAsync();
                var devicesVm = new DevicesViewModel(myDevices, confirmations);

                // Your friends, over the same helper: they see what you're playing, and you them.
                var friends = new Core.Friends.Friends(settings, peers.GetAsync);
                _ = friends.StartAsync();
                // Listening parties between friends: hosting tells guests what the player is doing;
                // a guest's queue plays the host's songs from where the host is.
                var partyHost = new Core.Party.PlayerPartySource(_runner, queuePlayback);
                var party = new Core.Party.ListeningParty(friends, partyHost, new Core.Party.QueuePartyPlayer(queuePlayback));
                party.Start();
                var friendsVm = new FriendsViewModel(friends, party, confirmations);

                var vm = new MainWindowViewModel(router, _store, mixStore, mixSwitcher, queuePlayback, _historyStore, _metaResolver, contentResolver, metaVm, toolRegistry, installers, eventsVm, controlsVm, _preview, _game, gameSettings, toasts, dialogManager, confirmations, devicesVm, friendsVm, lyrics, songSync);
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
                    friends.SetListening(Listening(t));
                };

                _enricher.TrackEnriched += t =>
                {
                    Dispatcher.UIThread.Post(() => vm.NowPlaying.Apply(t));
                    // A lookup can finish after its track was parked (the game routed elsewhere);
                    // don't put a title back up for a channel that's no longer playing.
                    if (_runner.ActiveSource != null) _game.NowPlaying?.Show(t);
                    if (_runner.ActiveSource != null) friends.SetListening(Listening(t));
                };

                // Nothing on the runner any more (stopped, or parked as the game switched channels):
                // the game shouldn't keep showing the last track. A channel that resumes re-announces
                // its track when it starts playing. (ActiveSourceChanged also fires with a null
                // factory when a channel starts, so check the runner itself.)
                _runner.ActiveSourceChanged += _ =>
                {
                    if (_runner.ActiveSource == null)
                    {
                        _game.NowPlaying?.Clear();
                        friends.SetListening(null);
                    }
                };

                desktop.ShutdownRequested += async (_, _) =>
                {
                    // First, and without waiting: the helper closes its connections, so friends and
                    // your other PCs see this one leave now. Nothing below is waited for before exit.
                    peers.Stop();
                    _eventExecutor?.Dispose();
                    _inputService?.Dispose();
                    _historyService?.Dispose(); // unsubscribes from the runner + flushes the final save
                    mixSwitcher.Dispose();
                    _preview?.Dispose();
                    if (_enricher != null) await _enricher.DisposeAsync();
                    if (_metaResolver != null) await _metaResolver.DisposeAsync();
                    await router.DisposeAsync(); // stops the engine; disposes parked channels and the player
                    if (_runner != null) await _runner.DisposeAsync();
                    // After the runner, so nothing is still sending audio to the game.
                    if (_game != null) await _game.DisposeAsync();
                    await party.DisposeAsync();
                    partyHost.Dispose();
                    await friends.DisposeAsync();
                    await myDevices.DisposeAsync();
                    await peers.DisposeAsync(); // stops the peer helper
                    // Dispose the container last — it owns the leaf singletons (stores, caches) and
                    // the plugins' services (Spotify's connection and players among them).
                    await provider.DisposeAsync();
                    settings.Dispose(); // writes any change still waiting to be saved
                    HorizonRadio.Core.Diagnostics.LogFile.Stop();
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
    // startup: relaunch with the new choice (already saved) and shut this instance down. The new
    // instance gets the same arguments, so it runs as the same profile.
    private static void Restart(IClassicDesktopStyleApplicationLifetime desktop)
    {
        if (Environment.ProcessPath is { } exe)
        {
            try { System.Diagnostics.Process.Start(exe, desktop.Args ?? []); }
            catch (Exception ex) { System.Diagnostics.Trace.WriteLine($"[hzn-app] restart failed: {ex.Message}"); }
        }
        desktop.Shutdown();
    }

    // What your friends see you playing: a real song only (not a station card before its first title).
    private static Core.Friends.Listening? Listening(Track t) =>
        t.Resolvable && !string.IsNullOrWhiteSpace(t.Title)
            ? new Core.Friends.Listening(t.Title, t.Artist, t.SourceDisplay, t.ExternalId)
            : null;

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
