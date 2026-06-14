using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using HorizonRadio.Core.Metadata;
using HorizonRadio.Core.Models;
using HorizonRadio.Core.ModInstall;
using HorizonRadio.Core.Sources;
using HorizonRadio.Core.Sources.Config;
using HorizonRadio.Core.Sources.Mixes;
using HorizonRadio.Core.Sources.Queue;
using HorizonRadio.UI.Services;
using HorizonRadio.UI.Tools;
using ShadUI;

namespace HorizonRadio.UI.ViewModels;

/// <summary>
/// Shell view model. Owns the three workspace VMs, the sidebar
/// expand/collapse state, and the IPC connection state. The actual
/// IPC client (separate service, lands next) drives the connection
/// state and forwards events to the right workspace VM.
/// </summary>
public sealed partial class MainWindowViewModel : ViewModelBase
{
    public NowPlayingViewModel NowPlaying { get; }
    public StatsViewModel Stats { get; }
    public ModManagerViewModel ModManager { get; } = new();

    // Both NowPlaying (for its inline source dropdown) and Sources need
    // the runner + store; the App creates them once and passes through.
    public SourcesViewModel Sources { get; }
    public MetadataViewModel Metadata { get; }
    public ToolsViewModel ToolsTab { get; }
    public EventsViewModel Events { get; }
    public ControlsViewModel Controls { get; }
    public MixesViewModel Mixes { get; }
    public ConsoleViewModel Console { get; } = new();
    public AboutViewModel About { get; } = new();

    /// <summary>The toggleable right-hand queue sidebar (Spotify-style). Shown when
    /// <see cref="IsQueueVisible"/> is set.</summary>
    public QueueViewModel Queue { get; }

    /// <summary>The full-width top bar (unified search box, today). Persistent across
    /// every workspace, like the player bar.</summary>
    public TopBarViewModel TopBar { get; }

    /// <summary>The full search page — a workspace with no nav item, reached by
    /// submitting a query from <see cref="TopBar"/>.</summary>
    public SearchViewModel Search { get; }

    /// <summary>Toast host bound in <c>MainWindow.axaml</c>; view models raise
    /// transient notifications through it (e.g. output-unavailable errors).</summary>
    public ToastManager ToastManager { get; }

    /// <summary>Dialog host bound in <c>MainWindow.axaml</c>; drives modal dialogs
    /// (e.g. the player-bar quick-play prompt). Custom dialog content must be
    /// registered with it (see App) — ShadUI maps dialog VMs to views via its own
    /// registry, not the app ViewLocator.</summary>
    public DialogManager DialogManager { get; }

    public MainWindowViewModel()
    {
        ToastManager = new ToastManager();
        DialogManager = new DialogManager();
        Sources = new SourcesViewModel();
        NowPlaying = new NowPlayingViewModel();
        Stats = new StatsViewModel();
        Metadata = new MetadataViewModel();
        ToolsTab = new ToolsViewModel();
        Events = new EventsViewModel();
        Controls = new ControlsViewModel();
        Mixes = new MixesViewModel();
        Queue = new QueueViewModel();
        TopBar = new TopBarViewModel();
        Search = new SearchViewModel();
        HookModBanner();
    }

    public MainWindowViewModel(SourceRunner runner,
                               SourceConfigStore store,
                               MixStore mixStore,
                               MixSwitcher mixSwitcher,
                               QueuePlayback queue,
                               MetadataResolver metaResolver,
                               Core.Sources.Mixes.MixContentResolver contentResolver,
                               MetadataViewModel metadata,
                               ToolRegistry registry,
                               System.Collections.Generic.IEnumerable<IToolInstaller> installers,
                               EventsViewModel events,
                               ControlsViewModel controls,
                               Core.Audio.PreviewController preview,
                               ToastManager toasts,
                               DialogManager dialogManager)
    {
        ToastManager = toasts;
        DialogManager = dialogManager;
        Sources = new SourcesViewModel(runner, store, registry);
        var station = new StationTargetViewModel(store);
        NowPlaying = new NowPlayingViewModel(runner, store, mixStore, mixSwitcher, queue, station, preview, toasts, DialogManager);
        Stats = new StatsViewModel(runner);
        Metadata = metadata;
        ToolsTab = new ToolsViewModel(registry, installers);
        Events = events;
        Controls = controls;
        Mixes = new MixesViewModel(mixStore, mixSwitcher, DialogManager, contentResolver);
        Queue = new QueueViewModel(queue, DialogManager, metaResolver);

        // Unified search: both the top-bar dropdown and the search page enqueue through
        // the shared SearchEnqueuer; submitting from the bar opens the page (index 11).
        // The shared context carries source names + the user's source priority so a merged
        // result row can label its sources and default Play to the preferred one.
        var searchEnqueuer = new SearchEnqueuer(queue, toasts);
        var searchContext = SearchSourceContext.Build(store);
        Search = new SearchViewModel(searchEnqueuer, searchContext);
        TopBar = new TopBarViewModel(searchEnqueuer, searchContext, ShowSearch);

        HookModBanner();
    }

    /// <summary>Open the full search page for a query (from the top bar's submit),
    /// reusing the results the bar already fetched. The page has no nav item, so this is
    /// the only way in.</summary>
    public void ShowSearch(string query, HorizonRadio.Core.Sources.UnifiedSearchResult result)
    {
        Search.Show(query, result);
        SelectedWorkspaceIndex = 11;
    }

    private void HookModBanner()
    {
        ModManager.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(ModManagerViewModel.Status))
            {
                OnPropertyChanged(nameof(ShowModBanner));
                OnPropertyChanged(nameof(ModBannerTitle));
                OnPropertyChanged(nameof(ModBannerText));
            }
        };
    }

    public bool ShowModBanner =>
        ModManager.Status is InstallationStatus.InstalledOther or InstallationStatus.NotInstalled;

    public string ModBannerTitle => ModManager.Status switch
    {
        InstallationStatus.InstalledOther => "Mod Update Available",
        InstallationStatus.NotInstalled => "Mod Not Installed",
        _ => "",
    };

    public string ModBannerText => ModManager.Status switch
    {
        InstallationStatus.InstalledOther => "A different version is installed in your game folder.",
        InstallationStatus.NotInstalled => "Horizon Radio isn't set up in your game yet.",
        _ => "",
    };

    // 0 = Now Playing, 1 = Sources, 2 = Metadata, 3 = Stats, 4 = Mod Manager, 5 = Tools, 6 = Events, 7 = Console, 8 = Controls, 9 = Mixes, 10 = About, 11 = Search
    [ObservableProperty] private int selectedWorkspaceIndex;

    public bool IsNowPlayingWorkspace => SelectedWorkspaceIndex == 0;
    public bool IsSourcesWorkspace => SelectedWorkspaceIndex == 1;
    public bool IsMetadataWorkspace => SelectedWorkspaceIndex == 2;
    public bool IsStatsWorkspace => SelectedWorkspaceIndex == 3;
    public bool IsModManagerWorkspace => SelectedWorkspaceIndex == 4;
    public bool IsToolsWorkspace => SelectedWorkspaceIndex == 5;
    public bool IsEventsWorkspace => SelectedWorkspaceIndex == 6;
    public bool IsConsoleWorkspace => SelectedWorkspaceIndex == 7;
    public bool IsControlsWorkspace => SelectedWorkspaceIndex == 8;
    public bool IsMixesWorkspace => SelectedWorkspaceIndex == 9;
    public bool IsAboutWorkspace => SelectedWorkspaceIndex == 10;
    public bool IsSearchWorkspace => SelectedWorkspaceIndex == 11;

    public string CurrentRoute => SelectedWorkspaceIndex switch
    {
        1 => "sources",
        2 => "metadata",
        3 => "stats",
        4 => "mod-manager",
        5 => "tools",
        6 => "events",
        7 => "console",
        8 => "controls",
        9 => "mixes",
        10 => "about",
        11 => "search", // no SidebarItem matches → nav shows nothing selected (intended)
        _ => "now-playing",
    };

    [ObservableProperty] private bool isSidebarExpanded = true;
    public bool IsSidebarCollapsed => !IsSidebarExpanded;

    /// <summary>Whether the right-hand queue sidebar is shown. Toggled from the
    /// player bar; off by default so the workspace gets the full width until asked.</summary>
    [ObservableProperty] private bool isQueueVisible;

    [ObservableProperty] private ConnectionState connection = ConnectionState.Disconnected;
    [ObservableProperty] private string connectionLabel = "Not connected to Forza Horizon 6";

    partial void OnSelectedWorkspaceIndexChanged(int value)
    {
        OnPropertyChanged(nameof(IsNowPlayingWorkspace));
        OnPropertyChanged(nameof(IsSourcesWorkspace));
        OnPropertyChanged(nameof(IsMetadataWorkspace));
        OnPropertyChanged(nameof(IsStatsWorkspace));
        OnPropertyChanged(nameof(IsModManagerWorkspace));
        OnPropertyChanged(nameof(IsToolsWorkspace));
        OnPropertyChanged(nameof(IsEventsWorkspace));
        OnPropertyChanged(nameof(IsConsoleWorkspace));
        OnPropertyChanged(nameof(IsControlsWorkspace));
        OnPropertyChanged(nameof(IsMixesWorkspace));
        OnPropertyChanged(nameof(IsAboutWorkspace));
        OnPropertyChanged(nameof(IsSearchWorkspace));
        OnPropertyChanged(nameof(CurrentRoute));
    }

    partial void OnIsSidebarExpandedChanged(bool value)
    {
        OnPropertyChanged(nameof(IsSidebarCollapsed));
    }

    [RelayCommand] private void ToggleSidebar() => IsSidebarExpanded = !IsSidebarExpanded;
    [RelayCommand] private void ShowNowPlaying() => SelectedWorkspaceIndex = 0;
    [RelayCommand] private void ShowSources() => SelectedWorkspaceIndex = 1;
    [RelayCommand] private void ShowMetadata() => SelectedWorkspaceIndex = 2;
    [RelayCommand] private void ShowStats() => SelectedWorkspaceIndex = 3;
    [RelayCommand] private void ShowModManager() => SelectedWorkspaceIndex = 4;
    [RelayCommand] private void ShowTools() => SelectedWorkspaceIndex = 5;
    [RelayCommand] private void ShowEvents() => SelectedWorkspaceIndex = 6;
    [RelayCommand] private void ShowConsole() => SelectedWorkspaceIndex = 7;
    [RelayCommand] private void ShowControls() => SelectedWorkspaceIndex = 8;
    [RelayCommand] private void ShowMixes() => SelectedWorkspaceIndex = 9;
    [RelayCommand] private void ShowAbout() => SelectedWorkspaceIndex = 10;

    public void SetConnection(ConnectionState state)
    {
        Connection = state;
        ConnectionLabel = state switch
        {
            ConnectionState.Connected => "Connected",
            ConnectionState.Connecting => "Connecting…",
            ConnectionState.Disconnected => "Disconnected",
            _ => "Unknown",
        };
        NowPlaying.SetConnectionState(state == ConnectionState.Connected);
        Stats.SetConnectionState(state == ConnectionState.Connected);
    }
}
