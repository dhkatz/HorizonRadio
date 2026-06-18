using HorizonRadio.Core.Metadata;

namespace HorizonRadio.Plugins.Abstractions;

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
}
