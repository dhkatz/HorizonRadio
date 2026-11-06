using HorizonRadio.Core.Games;
using HorizonRadio.Plugins.Abstractions;

namespace HorizonRadio.Game.ForzaHorizon6;

/// <summary>Forza Horizon 6, via the injected <c>version.dll</c> mod (Mods/ForzaHorizon6).</summary>
public sealed class Fh6GamePlugin : IGamePlugin
{
    public string Id => "fh6";

    public string DisplayName => "Forza Horizon 6";

    public int SortOrder => 10;

    public IGameIntegration Create(IPluginContext context) => new Fh6GameIntegration(DisplayName);
}
