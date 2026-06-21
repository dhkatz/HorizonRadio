using System.Collections.Generic;
using HorizonRadio.Core.Sources.Config;

namespace HorizonRadio.Core.Metadata.VocaDb;

public sealed class VocaDbProviderFactory : IMetadataProviderFactory
{
    public string Id => "vocadb";
    public string DisplayName => "VocaDB";
    public string? Description => "Free / no-credentials lookup against VocaDB — the community database for Vocaloid / UTAU / SynthV music. Catches producer tracks that aren't on iTunes/Spotify; art is the song's Niconico/YouTube image.";

    // Keyless, no configuration.
    public IReadOnlyList<ConfigField> Schema { get; } = [];

    public IMetadataProvider Create(ConfigValues values, MetadataCache cache) => new VocaDbProvider(cache);
}
