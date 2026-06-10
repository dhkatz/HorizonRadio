using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using HorizonRadio.Core.Metadata;
using HorizonRadio.Core.Models;
using HorizonRadio.Core.ModInstall;
using HorizonRadio.Core.Sources;
using HorizonRadio.Core.Sources.Config;
using HorizonRadio.Core.Sources.Profiles;
using HorizonRadio.UI.Tools;

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
    public ProfilesViewModel Profiles { get; }
    public ConsoleViewModel Console { get; } = new();

    public MainWindowViewModel()
    {
        Sources = new SourcesViewModel();
        NowPlaying = new NowPlayingViewModel();
        Stats = new StatsViewModel();
        Metadata = new MetadataViewModel();
        ToolsTab = new ToolsViewModel();
        Events = new EventsViewModel();
        Controls = new ControlsViewModel();
        Profiles = new ProfilesViewModel();
        HookModBanner();
    }

    public MainWindowViewModel(SourceRunner runner,
                               SourceConfigStore store,
                               SourceProfileStore profileStore,
                               ProfileSwitcher profileSwitcher,
                               MetadataViewModel metadata,
                               ToolRegistry registry,
                               System.Collections.Generic.IEnumerable<IToolInstaller> installers,
                               EventsViewModel events,
                               ControlsViewModel controls)
    {
        Sources = new SourcesViewModel(runner, store, registry);
        NowPlaying = new NowPlayingViewModel(runner, store, profileStore, profileSwitcher);
        Stats = new StatsViewModel(runner);
        Metadata = metadata;
        ToolsTab = new ToolsViewModel(registry, installers);
        Events = events;
        Controls = controls;
        Profiles = new ProfilesViewModel(profileStore, profileSwitcher, registry);
        HookModBanner();
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

    // 0 = Now Playing, 1 = Sources, 2 = Metadata, 3 = Stats, 4 = Mod Manager, 5 = Tools, 6 = Events, 7 = Console, 8 = Controls, 9 = Profiles
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
    public bool IsProfilesWorkspace => SelectedWorkspaceIndex == 9;

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
        9 => "profiles",
        _ => "now-playing",
    };

    [ObservableProperty] private bool isSidebarExpanded = true;
    public bool IsSidebarCollapsed => !IsSidebarExpanded;

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
        OnPropertyChanged(nameof(IsProfilesWorkspace));
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
    [RelayCommand] private void ShowProfiles() => SelectedWorkspaceIndex = 9;

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
