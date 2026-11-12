using System.Collections.Generic;
using HorizonRadio.Sdk;
using HorizonRadio.Sdk.Lyrics;
using HorizonRadio.Sdk.Metadata;

namespace HorizonRadio.Core.Metadata.VocaDb;

/// <summary>The VocaDB plugin: contributes the VocaDB metadata provider, and VocaDB's lyrics
/// (original, romanized, translated; never timed, so after the synced lyrics providers).</summary>
public sealed class VocaDbMetadataPlugin : IMetadataPlugin, ILyricsPlugin
{
    public string Id => "vocadb";
    public string DisplayName => "VocaDB";
    public int SortOrder => 40;
    public IReadOnlyList<IMetadataProviderFactory> Providers { get; } = [new VocaDbProviderFactory()];

    public ILyricsProvider CreateLyricsProvider(IPluginContext context) =>
        new VocaDbLyricsProvider(new VocaDbProvider(context.Cache));
}
