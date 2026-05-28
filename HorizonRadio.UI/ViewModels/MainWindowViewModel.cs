using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using HorizonRadio.Core.Metadata;
using HorizonRadio.Core.Models;
using HorizonRadio.Core.Sources;
using HorizonRadio.Core.Sources.Config;

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
    public StatsViewModel      Stats      { get; }
    public ModManagerViewModel ModManager { get; } = new();

    // Both NowPlaying (for its inline source dropdown) and Sources need
    // the runner + store; the App creates them once and passes through.
    public SourcesViewModel    Sources    { get; }
    public MetadataViewModel   Metadata   { get; }

    public MainWindowViewModel()
    {
        Sources    = new SourcesViewModel();
        NowPlaying = new NowPlayingViewModel();
        Stats      = new StatsViewModel();
        Metadata   = new MetadataViewModel();
    }

    public MainWindowViewModel(SourceRunner runner,
                               SourceConfigStore store,
                               MetadataViewModel metadata)
    {
        Sources    = new SourcesViewModel(runner, store);
        NowPlaying = new NowPlayingViewModel(runner, store);
        Stats      = new StatsViewModel(runner);
        Metadata   = metadata;
    }

    // 0 = Now Playing, 1 = Sources, 2 = Metadata, 3 = Stats, 4 = Mod Manager
    [ObservableProperty] private int selectedWorkspaceIndex;

    public bool IsNowPlayingWorkspace => SelectedWorkspaceIndex == 0;
    public bool IsSourcesWorkspace    => SelectedWorkspaceIndex == 1;
    public bool IsMetadataWorkspace   => SelectedWorkspaceIndex == 2;
    public bool IsStatsWorkspace      => SelectedWorkspaceIndex == 3;
    public bool IsModManagerWorkspace => SelectedWorkspaceIndex == 4;

    public string CurrentRoute => SelectedWorkspaceIndex switch
    {
        1 => "sources",
        2 => "metadata",
        3 => "stats",
        4 => "mod-manager",
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
        OnPropertyChanged(nameof(CurrentRoute));
    }

    partial void OnIsSidebarExpandedChanged(bool value)
    {
        OnPropertyChanged(nameof(IsSidebarCollapsed));
    }

    [RelayCommand] private void ToggleSidebar() => IsSidebarExpanded = !IsSidebarExpanded;
    [RelayCommand] private void ShowNowPlaying()  => SelectedWorkspaceIndex = 0;
    [RelayCommand] private void ShowSources()     => SelectedWorkspaceIndex = 1;
    [RelayCommand] private void ShowMetadata()    => SelectedWorkspaceIndex = 2;
    [RelayCommand] private void ShowStats()       => SelectedWorkspaceIndex = 3;
    [RelayCommand] private void ShowModManager()  => SelectedWorkspaceIndex = 4;

    public void SetConnection(ConnectionState state)
    {
        Connection = state;
        ConnectionLabel = state switch
        {
            ConnectionState.Connected    => "Connected",
            ConnectionState.Connecting   => "Connecting…",
            ConnectionState.Disconnected => "Disconnected",
            _ => "Unknown",
        };
        NowPlaying.SetConnectionState(state == ConnectionState.Connected);
        Stats.SetConnectionState(state == ConnectionState.Connected);
    }
}
