using System.Collections.Generic;
using HorizonRadio.Sdk;
using HorizonRadio.Sdk.Lyrics;
using HorizonRadio.Sdk.Metadata;

namespace HorizonRadio.Metadata.VocaDb;

/// <summary>The VocaDB plugin: contributes the VocaDB metadata provider, and VocaDB's lyrics
/// (original, romanized, translated; never timed, so after the synced lyrics providers).</summary>
public sealed class VocaDbMetadataPlugin(IMetadataCache cache, IMetadataTrace trace) : IMetadataPlugin, ILyricsPlugin
{
    public string Id => "vocadb";
    public string DisplayName => "VocaDB";
    public int SortOrder => 40;
    public IReadOnlyList<IMetadataProviderFactory> Providers { get; } = [new VocaDbProviderFactory(cache, trace)];

    public ILyricsProvider CreateLyricsProvider() =>
        new VocaDbLyricsProvider(new VocaDbProvider(cache, trace));
}
