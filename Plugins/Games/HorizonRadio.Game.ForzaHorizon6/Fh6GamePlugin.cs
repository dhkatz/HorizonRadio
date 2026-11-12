using HorizonRadio.Sdk;
using HorizonRadio.Sdk.Games;

namespace HorizonRadio.Game.ForzaHorizon6;

/// <summary>Forza Horizon 6, via the injected <c>version.dll</c> mod (Mods/ForzaHorizon6).</summary>
public sealed class Fh6GamePlugin(IPluginSettingsStore settings, IConfirmations confirmations, IPluginLog log) : IGamePlugin
{
    public string Id => "fh6";

    public string DisplayName => "Forza Horizon 6";

    public int SortOrder => 10;

    public IGameIntegration Create() =>
        new Fh6GameIntegration(DisplayName, settings.ForPlugin(Id, StationMap.IsSynced), confirmations, log);
}
