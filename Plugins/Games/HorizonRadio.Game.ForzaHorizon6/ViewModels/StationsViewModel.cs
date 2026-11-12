using System.Collections.ObjectModel;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using HorizonRadio.Core.Sources;
using HorizonRadio.Sdk.Games;

namespace HorizonRadio.Game.ForzaHorizon6.ViewModels;

/// <summary>One choice in a station's dropdown.</summary>
public sealed record StationOption(string Label, StationAssignment Assignment);

/// <summary>One choice in the switching-mode dropdown.</summary>
public sealed record SwitchModeOption(StationSwitchMode Mode, string Label, string Description);

/// <summary>
/// The Stations page: what each in-game station plays (the game's own music, Horizon Radio's
/// player, or a mix) and how mix stations behave while you're tuned elsewhere. Edits go straight
/// to the integration's <see cref="StationMap"/>, which persists them and re-routes playback.
/// </summary>
public sealed partial class StationsViewModel : ObservableObject
{
    private readonly Fh6GameIntegration _game;
    private bool _syncing;

    public StationsViewModel(Fh6GameIntegration game)
    {
        _game = game;
        selectedMode = Modes.First(m => m.Mode == game.Map.Mode);
        foreach (var station in StationCatalog.Names)
            Rows.Add(new StationRow(station, OnPicked));
        Refresh();

        // Both may fire off the UI thread (the store, the DLL's pipe reader).
        game.MixesChanged += () => Dispatcher.UIThread.Post(Refresh);
        game.Changed += () => Dispatcher.UIThread.Post(Refresh);
    }

    /// <summary>Designer-only ctor.</summary>
    public StationsViewModel() : this(new Fh6GameIntegration("Forza Horizon 6")) { }

    public ObservableCollection<StationRow> Rows { get; } = new();

    public IReadOnlyList<SwitchModeOption> Modes { get; } =
    [
        new(StationSwitchMode.Resume, "Pause and resume",
            "A mix station pauses while you're tuned elsewhere and picks up where it left off."),
        new(StationSwitchMode.Restart, "Start fresh",
            "A mix station starts its mix from the top each time you tune in."),
        new(StationSwitchMode.Live, "Live radio",
            "Mix stations keep playing while you're away, so you tune back in mid-show. Manual pause is off."),
    ];

    [ObservableProperty] private SwitchModeOption selectedMode;

    partial void OnSelectedModeChanged(SwitchModeOption value) => _game.Map.Mode = value.Mode;

    private void OnPicked(string station, StationAssignment assignment)
    {
        if (_syncing) return;
        _game.Map.Set(station, assignment);
    }

    // Rebuild every dropdown from the current mixes and assignments, and mark the station on air.
    private void Refresh()
    {
        _syncing = true;
        try
        {
            IReadOnlyList<StationOption> options =
            [
                new("Game audio", StationAssignment.Game),
                new("Horizon Radio player", StationAssignment.Player),
                .. _game.Mixes.Select(m => new StationOption(m.Name, StationAssignment.ForMix(m.Id))),
            ];
            var onAir = _game.CurrentStation;
            SelectedMode = Modes.First(m => m.Mode == _game.Map.Mode); // it may have changed on another PC
            foreach (var row in Rows)
            {
                var assignment = _game.Map.Get(row.Name);
                row.Options = options;
                row.Selected = options.FirstOrDefault(o => o.Assignment == assignment) ?? options[0];
                row.IsOnAir = row.Name == onAir;
            }
        }
        finally { _syncing = false; }
    }
}

/// <summary>A station and what it plays.</summary>
public sealed partial class StationRow(string name, Action<string, StationAssignment> onPicked) : ObservableObject
{
    public string Name { get; } = name;

    /// <summary>A note under the station's name, or null.</summary>
    public string? Hint { get; } = name == "Streamer Mode"
        ? "Only on air with Streamer Mode turned on in Forza's settings, which hides every other station."
        : null;

    public bool HasHint => Hint != null;

    [ObservableProperty] private IReadOnlyList<StationOption> options = [];

    [ObservableProperty] private StationOption? selected;

    /// <summary>True while this station is tuned in.</summary>
    [ObservableProperty] private bool isOnAir;

    partial void OnSelectedChanged(StationOption? value)
    {
        if (value != null) onPicked(Name, value.Assignment);
    }
}
