using HorizonRadio.Core.Metadata;
using HorizonRadio.Plugins.Abstractions;

namespace HorizonRadio.UI.Services;

/// <summary>
/// The host's <see cref="IPluginContext"/> — the shared services the app hands to plugin factories
/// when it builds them. For now it carries the metadata cache; it grows as more plugin kinds come
/// online (a tool resolver, HTTP client, diagnostics, cross-plugin service lookup).
/// </summary>
internal sealed class HostPluginContext(IMetadataCache cache) : IPluginContext
{
    public IMetadataCache Cache { get; } = cache;
}
