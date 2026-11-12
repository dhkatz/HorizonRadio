using System;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using HorizonRadio.Sdk.Games;

namespace HorizonRadio.UI.ViewModels;

/// <summary>
/// The player bar's game chip: which station the game is tuned to and what it plays there
/// ("Gacha City Radio · Horizon Radio player"). Stations are chosen in the game and set up on the
/// game's own page, which the chip opens. Hidden for a game without stations.
/// </summary>
public sealed partial class GameChipViewModel : ViewModelBase
{
    private readonly string _gameName;
    private readonly IStationTargeting? _stations;
    private readonly Action<string>? _openPage;

    public GameChipViewModel(string gameName, IStationTargeting? stations, Action<string>? openPage)
    {
        _gameName = gameName;
        _stations = stations;
        _openPage = openPage;
        title = gameName;
        detail = "Not connected";
        // Raised on any thread (the game's pipe reader, the mix store).
        if (stations != null) stations.Changed += () => Dispatcher.UIThread.Post(Refresh);
        Refresh();
    }

    /// <summary>Designer-only ctor.</summary>
    public GameChipViewModel() : this("Game", null, null) { }

    public bool IsAvailable => _stations != null;

    /// <summary>The tuned station, or the game's name when none is known.</summary>
    [ObservableProperty] private string title;

    /// <summary>What the tuned station plays, or the connection status.</summary>
    [ObservableProperty] private string detail;

    [ObservableProperty] private bool isConnected;

    partial void OnIsConnectedChanged(bool value) => Refresh();

    [RelayCommand]
    private void Open()
    {
        if (_stations?.PageId is { } pageId) _openPage?.Invoke(pageId);
    }

    private void Refresh()
    {
        Title = _stations?.CurrentStation ?? _gameName;
        Detail = _stations?.CurrentAssignment
                 ?? (IsConnected ? "Tune in to a station" : "Not connected");
    }
}
