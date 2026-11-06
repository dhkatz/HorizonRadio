using System;
using System.Collections.ObjectModel;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using HorizonRadio.Core.Games;

namespace HorizonRadio.UI.ViewModels;

/// <summary>
/// The player bar's "which in-game station Horizon Radio replaces" picker: a thin view over the
/// active game's <see cref="IStationTargeting"/>. The game owns the choice: it persists the default
/// station, layers the active mix's override on top, and keeps the game in sync. Picking here sets
/// the game's default.
/// </summary>
public sealed partial class StationTargetViewModel : ViewModelBase
{
    /// <summary>Picker entry meaning "replace whatever station is active".</summary>
    public const string AnyLabel = "Any station";

    private readonly IStationTargeting? _targeting;

    /// <summary>"Any station" + the game's stations.</summary>
    public ObservableCollection<string> Stations { get; }

    /// <summary>False when the active game has no stations to pick from.</summary>
    public bool IsAvailable => _targeting != null;

    [ObservableProperty] private string selectedStation;

    public StationTargetViewModel(IStationTargeting? targeting)
    {
        _targeting = targeting;
        Stations = new ObservableCollection<string> { AnyLabel };
        if (targeting != null)
        {
            foreach (var station in targeting.Stations) Stations.Add(station);
            // Mirror changes made elsewhere (raised on any thread).
            targeting.Changed += () => Dispatcher.UIThread.Post(SyncFromGame);
        }

        // Backing field, so seeding doesn't write the value straight back to the game.
        selectedStation = targeting?.DefaultStation ?? AnyLabel;
    }

    /// <summary>Designer-only ctor (so the Avalonia previewer can construct the
    /// player bar without a game).</summary>
    public StationTargetViewModel() : this(null) { }

    private void SyncFromGame()
    {
        var current = _targeting?.DefaultStation ?? AnyLabel;
        if (current != SelectedStation) SelectedStation = current;
    }

    partial void OnSelectedStationChanged(string value)
    {
        if (_targeting != null) _targeting.DefaultStation = value == AnyLabel ? null : value;
    }
}
