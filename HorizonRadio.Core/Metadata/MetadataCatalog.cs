using System.Collections.Generic;
using System.Linq;

namespace HorizonRadio.Core.Metadata;

/// <summary>
/// Parallel to <see cref="HorizonRadio.Core.Sources.SourceCatalog"/>:
/// every <see cref="IMetadataProviderFactory"/> the app knows about.
/// Includes a "None" entry so users can disable enrichment entirely.
/// </summary>
public static class MetadataCatalog
{
    /// <summary>Stable id for "no enrichment". Selected when the user
    /// wants Now Playing to show only what the source provides.</summary>
    public const string NoneId = "none";

    public static IReadOnlyList<IMetadataProviderFactory> All { get; } = new IMetadataProviderFactory[]
    {
        new SpotifyEnricherFactory(),
        new MusicBrainzEnricherFactory(),
    };

    public static IMetadataProviderFactory? Find(string id) =>
        All.FirstOrDefault(f => f.Id == id);
}
