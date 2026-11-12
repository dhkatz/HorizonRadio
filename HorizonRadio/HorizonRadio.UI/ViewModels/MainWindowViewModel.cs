using System.Collections.ObjectModel;
using System.Linq;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using HorizonRadio.Core.Games;
using HorizonRadio.Core.History;
using HorizonRadio.Core.Metadata;
using HorizonRadio.Core.Playback;
using HorizonRadio.Core.Sources.Config;
using HorizonRadio.Core.Sources.Mixes;
using HorizonRadio.Core.Sources.Queue;
using HorizonRadio.Core.Tools;
using HorizonRadio.Sdk;
using HorizonRadio.Sdk.Games;
using HorizonRadio.Sdk.Models;
using HorizonRadio.Sdk.Tools;
using HorizonRadio.UI.Services;
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

    /// <summary>Settings → Game: which game Horizon Radio runs with.</summary>
    public GameSettingsViewModel GameSettings { get; }

    /// <summary>False for "No Game": there's no game connection to show.</summary>
    public bool HasGame { get; } = true;

    private readonly IGameIntegration? _game;

    /// <summary>The active game's name: the sidebar section its pages are listed under.</summary>
    public string GameName { get; }

    /// <summary>Pages the active game integration contributes (e.g. FH6's Mods and Stats).</summary>
    public ObservableCollection<GamePageItem> GamePages { get; } = new();

    public bool HasGamePages => GamePages.Count > 0;

    /// <summary>The game page shown when <see cref="IsGamePageWorkspace"/>.</summary>
    [ObservableProperty] private GamePageItem? selectedGamePage;

    // Both NowPlaying (for its inline source dropdown) and Sources need
    // the playback router + store; the App creates them once and passes through.
    public SourcesViewModel Sources { get; }
    public MetadataViewModel Metadata { get; }
    public ToolsViewModel ToolsTab { get; }
    public EventsViewModel Events { get; }
    public ControlsViewModel Controls { get; }
    public MixesViewModel Mixes { get; }
    public DevicesViewModel Devices { get; }
    public FriendsViewModel Friends { get; }
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

    /// <summary>The play-history workspace.</summary>
    public HistoryViewModel History { get; }

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
        GameSettings = new GameSettingsViewModel();
        GameName = "Game";
        Metadata = new MetadataViewModel();
        ToolsTab = new ToolsViewModel();
        Events = new EventsViewModel();
        Controls = new ControlsViewModel();
        Mixes = new MixesViewModel();
        Devices = new DevicesViewModel();
        Friends = new FriendsViewModel();
        Queue = new QueueViewModel();
        TopBar = new TopBarViewModel();
        Search = new SearchViewModel();
        History = new HistoryViewModel();
    }

    public MainWindowViewModel(PlaybackRouter playback,
                               SourceConfigStore store,
                               MixStore mixStore,
                               MixSwitcher mixSwitcher,
                               QueuePlayback queue,
                               PlayHistoryStore historyStore,
                               MetadataResolver metaResolver,
                               Core.Sources.Mixes.MixContentResolver contentResolver,
                               MetadataViewModel metadata,
                               ToolRegistry registry,
                               System.Collections.Generic.IEnumerable<IToolInstaller> installers,
                               EventsViewModel events,
                               ControlsViewModel controls,
                               Core.Audio.PreviewController preview,
                               IGameIntegration game,
                               GameSettingsViewModel gameSettings,
                               ToastManager toasts,
                               DialogManager dialogManager,
                               IConfirmations confirmations,
                               DevicesViewModel devices,
                               FriendsViewModel friends,
                               Core.Lyrics.LyricsService? lyrics = null,
                               Core.Fingerprints.LiveSongSync? songSync = null)
    {
        Devices = devices;
        Friends = friends;
        ToastManager = toasts;
        DialogManager = dialogManager;
        _game = game;
        GameName = game.DisplayName;
        GameSettings = gameSettings;
        HasGame = game is not NoGameIntegration;
        ConnectionLabel = HasGame ? $"Not connected to {GameName}" : "Playing on this PC";
        Sources = new SourcesViewModel(playback, store, registry);

        // Anywhere a song shows (History, search, the queue, the song playing now, a mix's entries)
        // it can be added to the queue or a mix, through this one place. Mix changes go through the
        // Mixes tab, so a mix open there shows them.
        var songs = new SongActions(queue, mixStore, metaResolver, toasts);

        NowPlaying = new NowPlayingViewModel(playback, store, mixStore, mixSwitcher,
            game.Output != null ? GameName : null, preview, toasts, DialogManager, lyrics, songSync)
        {
            Songs = songs,
        };
        Metadata = metadata;
        ToolsTab = new ToolsViewModel(registry, installers, confirmations);
        Events = events;
        Controls = controls;
        // Mix entries show as what they are (titles, artists, covers), looked up through the same
        // sources and metadata catalogs the queue uses, and kept on disk between sessions.
        Mixes = new MixesViewModel(mixStore, mixSwitcher, playback.Sources, DialogManager,
            new Core.Sources.Mixes.MixEntryDescriber(contentResolver, metaResolver, new Core.Sources.Mixes.MixEntryInfoStore()),
            songs, confirmations);
        songs.Editor = Mixes;
        songs.ShowMixes = () => SelectedWorkspaceIndex = 9;
        Queue = new QueueViewModel(queue, DialogManager, metaResolver, songs, confirmations);

        // Unified search: both the top-bar dropdown and the search page enqueue through
        // the shared SearchEnqueuer; submitting from the bar opens the page (index 11).
        // The shared context carries source names + the user's source priority so a merged
        // result row can label its sources and default Play to the preferred one.
        var searchEnqueuer = new SearchEnqueuer(queue, toasts) { Songs = songs };
        var searchContext = SearchSourceContext.Build(store.Settings, playback.Sources);
        Search = new SearchViewModel(searchEnqueuer, searchContext);
        TopBar = new TopBarViewModel(searchEnqueuer, searchContext, ShowSearch);

        // History replays directly when it can; otherwise it re-searches the song on the
        // full search page (the same picker), so no-locator (radio) songs stay playable.
        History = new HistoryViewModel(historyStore, queue, metaResolver, OpenSearchFor, toasts, songs, confirmations);

        // The game's own pages plus the host's game-related ones (Events, when the game reports
        // any), under the game's name in the sidebar.
        var pages = game.Pages.ToList();
        if (game.EventCatalog.Count > 0)
            pages.Add(new GamePage("host-events", "Events", "FlashOutline", Events, GamePage.EventsOrder));
        foreach (var page in pages.OrderBy(p => p.Order)) GamePages.Add(new GamePageItem(page));

        NowPlaying.GameChip = new GameChipViewModel(GameName, game.Stations, id => ShowGamePage(FindGamePage(id)));
        game.NoticeChanged += () =>
        {
            OnPropertyChanged(nameof(ShowGameNotice));
            OnPropertyChanged(nameof(GameNoticeTitle));
            OnPropertyChanged(nameof(GameNoticeText));
            OnPropertyChanged(nameof(GameNoticeAction));
        };
    }

    /// <summary>Open the full search page for a query (from the top bar's submit),
    /// reusing the results the bar already fetched. The page has no nav item, so this is
    /// the only way in.</summary>
    public void ShowSearch(string query, HorizonRadio.Core.Sources.UnifiedSearchResult result)
    {
        Search.Show(query, result);
        SelectedWorkspaceIndex = 11;
    }

    /// <summary>Open the search page for a fresh query (no precomputed results) — the History
    /// tab's "find this song again" path when a played song has no re-addressable origin.</summary>
    public void OpenSearchFor(string query)
    {
        SelectedWorkspaceIndex = 11;
        _ = Search.RunAsync(query);
    }

    // The game's setup notice (e.g. FH6's "Mod Not Installed"), shown as a banner on every page.
    public bool ShowGameNotice => _game?.Notice != null;
    public string GameNoticeTitle => _game?.Notice?.Title ?? "";
    public string GameNoticeText => _game?.Notice?.Message ?? "";
    public string GameNoticeAction => FindGamePage(_game?.Notice?.PageId) is { } page ? $"Open {page.Title}" : "";

    [RelayCommand] private void OpenGameNotice() => ShowGamePage(FindGamePage(_game?.Notice?.PageId));

    private GamePageItem? FindGamePage(string? id) => GamePages.FirstOrDefault(p => p.Id == id);

    // 0 = Now Playing, 1 = Sources, 2 = Metadata, 5 = Tools, 7 = Console, 8 = Controls,
    // 9 = Mixes, 10 = About, 11 = Search, 12 = History, 13 = a game page (SelectedGamePage),
    // 14 = Settings → Game, 15 = Settings → Devices, 16 = Friends.
    // Retired: 3, 4 (Stats, Mods: now the FH6 plugin's pages) and 6 (Events: a game page now).
    [ObservableProperty] private int selectedWorkspaceIndex;

    private const int GamePageWorkspace = 13;

    public bool IsNowPlayingWorkspace => SelectedWorkspaceIndex == 0;
    public bool IsSourcesWorkspace => SelectedWorkspaceIndex == 1;
    public bool IsMetadataWorkspace => SelectedWorkspaceIndex == 2;
    public bool IsToolsWorkspace => SelectedWorkspaceIndex == 5;
    public bool IsConsoleWorkspace => SelectedWorkspaceIndex == 7;
    public bool IsControlsWorkspace => SelectedWorkspaceIndex == 8;
    public bool IsMixesWorkspace => SelectedWorkspaceIndex == 9;
    public bool IsAboutWorkspace => SelectedWorkspaceIndex == 10;
    public bool IsSearchWorkspace => SelectedWorkspaceIndex == 11;
    public bool IsHistoryWorkspace => SelectedWorkspaceIndex == 12;
    public bool IsGamePageWorkspace => SelectedWorkspaceIndex == GamePageWorkspace;
    public bool IsGameSettingsWorkspace => SelectedWorkspaceIndex == 14;
    public bool IsDevicesWorkspace => SelectedWorkspaceIndex == 15;
    public bool IsFriendsWorkspace => SelectedWorkspaceIndex == 16;

    public string CurrentRoute => SelectedWorkspaceIndex switch
    {
        1 => "sources",
        2 => "metadata",
        5 => "tools",
        7 => "console",
        8 => "controls",
        9 => "mixes",
        10 => "about",
        11 => "search", // no SidebarItem matches → nav shows nothing selected (intended)
        12 => "history",
        GamePageWorkspace => SelectedGamePage?.Route ?? "now-playing",
        14 => "game-settings",
        15 => "devices",
        16 => "friends",
        _ => "now-playing",
    };

    [ObservableProperty] private bool isSidebarExpanded = true;
    public bool IsSidebarCollapsed => !IsSidebarExpanded;

    /// <summary>Whether the right-hand queue sidebar is shown. Toggled from the
    /// player bar; off by default so the workspace gets the full width until asked.</summary>
    [ObservableProperty] private bool isQueueVisible;

    [ObservableProperty] private ConnectionState connection = ConnectionState.Disconnected;
    [ObservableProperty] private string connectionLabel = "Not connected";

    partial void OnSelectedWorkspaceIndexChanged(int value)
    {
        OnPropertyChanged(nameof(IsNowPlayingWorkspace));
        OnPropertyChanged(nameof(IsSourcesWorkspace));
        OnPropertyChanged(nameof(IsMetadataWorkspace));
        OnPropertyChanged(nameof(IsToolsWorkspace));
        OnPropertyChanged(nameof(IsConsoleWorkspace));
        OnPropertyChanged(nameof(IsControlsWorkspace));
        OnPropertyChanged(nameof(IsMixesWorkspace));
        OnPropertyChanged(nameof(IsAboutWorkspace));
        OnPropertyChanged(nameof(IsSearchWorkspace));
        OnPropertyChanged(nameof(IsHistoryWorkspace));
        OnPropertyChanged(nameof(IsGamePageWorkspace));
        OnPropertyChanged(nameof(IsGameSettingsWorkspace));
        OnPropertyChanged(nameof(IsDevicesWorkspace));
        OnPropertyChanged(nameof(IsFriendsWorkspace));
        OnPropertyChanged(nameof(CurrentRoute));
        UpdateGamePageSelection();

        // Showing History gives any rows that couldn't resolve earlier (e.g. a search source that
        // connected after startup) another chance to find art / playable sources.
        if (value == 12) History.RefreshEnrichment();
    }

    partial void OnIsSidebarExpandedChanged(bool value)
    {
        OnPropertyChanged(nameof(IsSidebarCollapsed));
    }

    [RelayCommand] private void ToggleSidebar() => IsSidebarExpanded = !IsSidebarExpanded;
    [RelayCommand] private void ShowNowPlaying() => SelectedWorkspaceIndex = 0;
    [RelayCommand] private void ShowSources() => SelectedWorkspaceIndex = 1;
    [RelayCommand] private void ShowMetadata() => SelectedWorkspaceIndex = 2;
    [RelayCommand] private void ShowTools() => SelectedWorkspaceIndex = 5;
    [RelayCommand] private void ShowConsole() => SelectedWorkspaceIndex = 7;
    [RelayCommand] private void ShowControls() => SelectedWorkspaceIndex = 8;
    [RelayCommand] private void ShowMixes() => SelectedWorkspaceIndex = 9;
    [RelayCommand] private void ShowAbout() => SelectedWorkspaceIndex = 10;
    [RelayCommand] private void ShowHistory() => SelectedWorkspaceIndex = 12;
    [RelayCommand] private void ShowGameSettings() => SelectedWorkspaceIndex = 14;
    [RelayCommand] private void ShowDevices() => SelectedWorkspaceIndex = 15;
    [RelayCommand] private void ShowFriends() => SelectedWorkspaceIndex = 16;

    [RelayCommand]
    private void ShowGamePage(GamePageItem? page)
    {
        if (page == null) return;
        SelectedGamePage = page;
        // Switching between two game pages leaves the index at 13, so refresh explicitly.
        if (SelectedWorkspaceIndex == GamePageWorkspace)
        {
            OnPropertyChanged(nameof(CurrentRoute));
            UpdateGamePageSelection();
        }
        else
        {
            SelectedWorkspaceIndex = GamePageWorkspace;
        }
    }

    private void UpdateGamePageSelection()
    {
        foreach (var page in GamePages)
            page.IsSelected = SelectedWorkspaceIndex == GamePageWorkspace && ReferenceEquals(page, SelectedGamePage);
    }

    public void SetConnection(ConnectionState state)
    {
        if (!HasGame) return; // "No Game" never connects; keep "Playing on this PC"
        Connection = state;
        ConnectionLabel = state switch
        {
            ConnectionState.Connected => "Connected",
            ConnectionState.Connecting => "Connecting…",
            ConnectionState.Disconnected => "Disconnected",
            _ => "Unknown",
        };
        NowPlaying.SetConnectionState(state == ConnectionState.Connected);
        NowPlaying.GameChip.IsConnected = state == ConnectionState.Connected;
    }
}
