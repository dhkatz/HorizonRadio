using HorizonRadio.Core.Plugins;
using HorizonRadio.Core.Settings;
using HorizonRadio.Sdk;
using HorizonRadio.Sdk.Metadata;

namespace HorizonRadio.UI.Services;

/// <summary>
/// The host's <see cref="IPluginContext"/> — the shared services the app hands to plugin factories
/// when it builds them (game integrations, lyrics and metadata providers). It isn't growing:
/// plugins now take services in their constructors from the container, and this goes once the
/// factories that take it do (docs/modular-architecture.md, step 5).
/// </summary>
internal sealed class HostPluginContext(IMetadataCache cache, SettingsStore settings, IConfirmations confirmations) : IPluginContext
{
    public IMetadataCache Cache { get; } = cache;

    public IConfirmations Confirmations { get; } = confirmations;

    public IPluginSettings GetSettings(string pluginId) => PluginSettings.For(settings, pluginId);

    public IPluginSettings GetSettings(string pluginId, System.Func<string, bool> synced) => PluginSettings.For(settings, pluginId, synced);
}
