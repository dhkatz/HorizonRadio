using HorizonRadio.Core.Metadata;

namespace HorizonRadio.Plugins.Abstractions;

/// <summary>
/// A metadata plugin: contributes one or more <see cref="IMetadataProviderFactory"/> to the
/// resolver. The host discovers metadata plugins and aggregates their providers into the pipeline.
/// The envelope will grow further metadata-pipeline extension points (title extractors, kanji
/// romanizers) as those capabilities land; for now a plugin contributes providers.
/// </summary>
public interface IMetadataPlugin : IPlugin
{
    /// <summary>The provider factories this plugin contributes, in priority order.</summary>
    IReadOnlyList<IMetadataProviderFactory> Providers { get; }
}
