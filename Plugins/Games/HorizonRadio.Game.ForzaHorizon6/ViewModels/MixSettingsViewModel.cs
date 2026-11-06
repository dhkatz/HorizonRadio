using CommunityToolkit.Mvvm.ComponentModel;
using HorizonRadio.Core.Games;

namespace HorizonRadio.Game.ForzaHorizon6.ViewModels;

/// <summary>
/// FH6's section of the mix editor: which station this mix replaces, or the default. Stored with
/// the mix as <c>{ "station": "…" }</c> under the plugin's id.
/// </summary>
public sealed partial class MixSettingsViewModel : ObservableObject, IGameMixEditor
{
    /// <summary>Choice meaning "no override: use the default station".</summary>
    public const string UseDefault = "Use default station";

    internal const string StationKey = "station";

    public MixSettingsViewModel(IReadOnlyList<string> stations, IReadOnlyDictionary<string, string>? settings)
    {
        Options = [UseDefault, .. stations];
        var saved = settings?.GetValueOrDefault(StationKey);
        selectedStation = saved != null && stations.Contains(saved) ? saved : UseDefault;
    }

    public IReadOnlyList<string> Options { get; }

    [ObservableProperty] private string selectedStation;

    public object ViewModel => this;

    public IReadOnlyDictionary<string, string>? Settings =>
        SelectedStation == UseDefault ? null : new Dictionary<string, string> { [StationKey] = SelectedStation };
}
