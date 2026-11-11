using HorizonRadio.Core.Plugins;
using HorizonRadio.Core.Settings;
using HorizonRadio.Sdk;
using HorizonRadio.Sdk.Metadata;

namespace HorizonRadio.UI.Services;

/// <summary>
/// The host's <see cref="IPluginContext"/> — the shared services the app hands to plugin factories
/// when it builds them. For now it carries the metadata cache; it grows as more plugin kinds come
/// online (a tool resolver, HTTP client, diagnostics, cross-plugin service lookup).
/// </summary>
internal sealed class HostPluginContext(IMetadataCache cache, SettingsStore settings) : IPluginContext
{
    public IMetadataCache Cache { get; } = cache;

    public IPluginSettings GetSettings(string pluginId) => PluginSettings.For(settings, pluginId);

    public IPluginSettings GetSettings(string pluginId, System.Func<string, bool> synced) => PluginSettings.For(settings, pluginId, synced);
}
