namespace HorizonRadio.Sdk;

/// <summary>
/// What the host hands a game plugin when it builds the game integration
/// (<see cref="IGamePlugin.Create"/>): the plugin's settings and a way to ask the user before
/// something destructive. Being replaced: plugins take host services in their constructors from the
/// host's container (see <see cref="IPluginModule"/>), and this goes once game plugins do too.
/// </summary>
public interface IPluginContext
{
    /// <summary>The persisted settings belonging to the plugin with id <paramref name="pluginId"/>
    /// (pass your own <see cref="IPlugin.Id"/>). They stay on this PC.</summary>
    IPluginSettings GetSettings(string pluginId);

    /// <summary>The plugin's settings, where the keys <paramref name="synced"/> says yes to follow the
    /// user to their other PCs (when they've paired them): preferences, not paths or facts about this
    /// machine. Watch <see cref="IPluginSettings.Changed"/> to follow a change made elsewhere.</summary>
    IPluginSettings GetSettings(string pluginId, System.Func<string, bool> synced) => GetSettings(pluginId);

    /// <summary>Asks the user before something that can't be undone (uninstalling, deleting).</summary>
    IConfirmations Confirmations => ProceedWithoutAsking.Instance;
}
