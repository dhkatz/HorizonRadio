using System;
using System.Linq;
using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Avalonia.Threading;
using HorizonRadio.Core.Diagnostics;
using HorizonRadio.Core.Hosting;
using HorizonRadio.Core.Tools;
using HorizonRadio.Platform;
using HorizonRadio.Sdk;
using HorizonRadio.UI.Services;
using HorizonRadio.UI.ViewModels;
using HorizonRadio.UI.Views;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using ShadUI;

namespace HorizonRadio.UI;

public partial class App : Application
{
    public override void Initialize()
    {
        AvaloniaXamlLoader.Load(this);
    }

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            // ShadUI maps dialog view models to views through its own registry (not the app's
            // ViewLocator), so custom dialog content must be registered.
            var dialogManager = new DialogManager();
            dialogManager.Register<QuickPlayDialogView, QuickPlayDialogViewModel>();
            dialogManager.Register<QueueAddModeDialogView, QueueAddModeDialogViewModel>();
            var confirmations = new DialogConfirmations(dialogManager);
            var toasts = new ToastManager();

            // Core starts everything (settings, the container with the plugins, the engine); the
            // window only adds itself: it asks with dialogs and tells with toasts (Core's defaults go
            // ahead without asking, and log). Built inside the desktop branch so the XAML designer
            // (which never enters here) is unaffected.
            var host = HorizonHost.Build(new HorizonHostOptions
            {
                Platform = HostPlatforms.Current(),
                ConfigureServices = services =>
                {
                    services.Replace(ServiceDescriptor.Singleton<IConfirmations>(confirmations));
                    services.Replace(ServiceDescriptor.Singleton<INotifications>(new ToastNotifications(toasts)));
                },
            });
            // Stopping takes a moment (the engine in order, then the container and the settings);
            // nothing waits for it before the process exits.
            desktop.ShutdownRequested += async (_, _) => await host.DisposeAsync();

            // The active game: one runs at a time, chosen by the user (a game plugin, or "No Game"
            // for a plain player). It's wired into the audio pipeline, events, and pages at startup,
            // so switching games restarts the app (Settings → Game).
            if (host.ChosenGame() is { } gameId)
            {
                Compose(gameId, showNow: false);
            }
            else
            {
                // First run (or the saved game was uninstalled): ask before building anything
                // game-related, then carry on with the choice.
                var firstRun = new FirstRunViewModel(host.Games, host.Settings);
                var picker = new FirstRunWindow { DataContext = firstRun };
                firstRun.Chosen += id =>
                {
                    Compose(id, showNow: true);
                    picker.Close();
                };
                desktop.MainWindow = picker;
            }

            // The window over the engine for the chosen game; runs now, or once the picker returns.
            void Compose(string chosenGameId, bool showNow)
            {
                var engine = host.StartEngine(chosenGameId);
                var services = host.Services;
                var tools = services.GetRequiredService<ToolCatalog>();
                var installers = tools.Plugins.Select(t => t.Installer).ToArray();

                var metaVm = new MetadataViewModel(engine.MetadataConfig, engine.Metadata);
                var eventsVm = new EventsViewModel(engine.EventRules, engine.Events, engine.Game.EventCatalog,
                    engine.Game.EventsHint, engine.Sources);
                var controlsVm = new ControlsViewModel(engine.InputBindings, engine.Input, engine.Mixes);
                var gameSettings = new GameSettingsViewModel(host.Games, host.Settings, chosenGameId);
                gameSettings.RestartRequested += () => Restart(desktop);
                var devicesVm = new DevicesViewModel(engine.Devices, confirmations);
                var friendsVm = new FriendsViewModel(engine.Friends, engine.Party, confirmations);

                var vm = new MainWindowViewModel(engine.Router, engine.SourceConfig, engine.Mixes, engine.MixSwitcher,
                    engine.Player, engine.HistoryStore, engine.Metadata, engine.MixEntries, metaVm,
                    services.GetRequiredService<ToolRegistry>(), installers, eventsVm, controlsVm, engine.Preview,
                    engine.Game, gameSettings, toasts, dialogManager, confirmations, devicesVm, friendsVm,
                    new AboutViewModel(services.GetRequiredService<MetadataTrace>()), engine.Lyrics, engine.SongSync);
                var mainWindow = new MainWindow { DataContext = vm };
                desktop.MainWindow = mainWindow;
                // Composed after startup (the first-run picker), so show it ourselves.
                if (showNow) mainWindow.Show();

                // What the window shows follows the engine, which raises on its own threads.
                engine.Game.StateChanged += state => Dispatcher.UIThread.Post(() => vm.SetConnection(state));
                engine.Runner.TrackChanged += t => Dispatcher.UIThread.Post(() => vm.NowPlaying.Apply(t));
                engine.Enricher.TrackEnriched += t => Dispatcher.UIThread.Post(() => vm.NowPlaying.Apply(t));
                engine.Start();

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
