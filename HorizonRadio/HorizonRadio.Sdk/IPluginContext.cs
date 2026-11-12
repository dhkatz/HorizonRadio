using HorizonRadio.Sdk.Metadata;

namespace HorizonRadio.Sdk;

/// <summary>
/// Host services handed to a plugin factory when it builds an instance. It decouples a plugin from
/// the host's concrete types: the factory receives this instead of, say, the concrete metadata
/// cache, so the plugin contract can live in the SDK without referencing the host engine.
///
/// It grows as more plugin kinds come online (a tool resolver, an HTTP client, diagnostics, a
/// cross-plugin service lookup); for now it exposes the metadata cache, which is all a metadata
/// provider needs from the host.
/// </summary>
public interface IPluginContext
{
    /// <summary>The shared on-disk metadata cache (see <see cref="IMetadataCache"/>).</summary>
    IMetadataCache Cache { get; }

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
