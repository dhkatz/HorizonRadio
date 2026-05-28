using System.Collections.Generic;
using HorizonRadio.Core.Sources.Config;

namespace HorizonRadio.Core.Metadata;

/// <summary>
/// Parallel to <see cref="HorizonRadio.Core.Sources.IAudioSourceFactory"/>:
/// describes one metadata enrichment backend and constructs a
/// configured <see cref="IMetadataEnricher"/> on demand.
///
/// Schema lets the UI auto-render a config form for each provider
/// (Spotify needs client id + secret; MusicBrainz works credential-
/// less). The Metadata tab uses the same ConfigField infrastructure
/// the Sources tab does.
/// </summary>
public interface IMetadataProviderFactory
{
    string Id { get; }
    string DisplayName { get; }
    string? Description { get; }
    IReadOnlyList<ConfigField> Schema { get; }

    /// <summary>Construct an enricher with the user-supplied config.
    /// May throw on invalid config (missing credentials, etc.); the
    /// service surfaces the message to the UI. The cache is shared
    /// across all providers (entries are namespaced by enricher id).</summary>
    IMetadataEnricher Create(ConfigValues values, MetadataCache cache);
}
