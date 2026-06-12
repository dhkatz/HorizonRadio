using System;
using System.Collections.ObjectModel;
using System.Linq;
using CommunityToolkit.Mvvm.ComponentModel;
using HorizonRadio.Core.Sources;
using HorizonRadio.Core.Sources.Config;

namespace HorizonRadio.UI.ViewModels;

/// <summary>
/// App-level "which in-game station Horizon Radio replaces" state. The target
/// station is global — it isn't a per-source or per-mix setting (a mix may
/// later carry its own override; see the mixes work) — so it lives in its own
/// small view model surfaced in the player bar, rather than buried in the
/// Sources tab. Persisted in <see cref="SourceConfigStore.TargetStation"/>;
/// App wires <see cref="TargetStationChanged"/> to push the choice to the DLL.
/// </summary>
public sealed partial class StationTargetViewModel : ViewModelBase
{
    private readonly SourceConfigStore _store;

    /// <summary>"Any station" + the fixed FH6 list.</summary>
    public ObservableCollection<string> Stations { get; } = new(StationCatalog.All);

    /// <summary>Raised when the user picks a different station to replace; App
    /// pushes it to the DLL over IPC.</summary>
    public event Action<string>? TargetStationChanged;

    [ObservableProperty] private string selectedStation;

    public StationTargetViewModel(SourceConfigStore store)
    {
        _store = store;

        // Assign the backing field directly so seeding the saved choice doesn't
        // fire OnSelectedStationChanged (which would re-persist and push before
        // App has wired TargetStationChanged).
        var saved = store.TargetStation;
        selectedStation = !string.IsNullOrEmpty(saved) && StationCatalog.All.Contains(saved)
            ? saved!
            : StationCatalog.AnyLabel;
    }

    /// <summary>Designer-only ctor (so the Avalonia previewer can construct the
    /// player bar without a real store).</summary>
    public StationTargetViewModel() : this(new SourceConfigStore()) { }

    partial void OnSelectedStationChanged(string value)
    {
        _store.TargetStation = value;
        _store.SaveToDisk();
        TargetStationChanged?.Invoke(value);
    }
}
