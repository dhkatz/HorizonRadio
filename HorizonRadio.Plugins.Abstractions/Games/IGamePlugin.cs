using HorizonRadio.Core.Games;

namespace HorizonRadio.Plugins.Abstractions;

/// <summary>
/// A game plugin: connects Horizon Radio to one game. The host discovers game plugins and runs
/// exactly one integration at a time (the active game); it builds it once at startup via
/// <see cref="Create"/> and owns the result's lifetime.
///
/// The plugin is the Horizon Radio side of the integration. Whatever runs inside the game (a native
/// DLL, a scripting-runtime mod, nothing at all) is the plugin's business and lives under
/// <c>Mods/&lt;Game&gt;/</c>; the host only ever sees <see cref="IGameIntegration"/>.
/// </summary>
public interface IGamePlugin : IPlugin
{
    /// <summary>Build the runtime integration. Must not connect or start background work —
    /// the host wires its event handlers first, then calls <see cref="IGameIntegration.Start"/>.</summary>
    IGameIntegration Create(IPluginContext context);
}
