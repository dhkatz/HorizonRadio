using System.Collections.Generic;
using HorizonRadio.Sdk;
using HorizonRadio.Sdk.Metadata;

namespace HorizonRadio.Metadata.Apple;

/// <summary>The Apple/iTunes metadata plugin — contributes the iTunes provider.</summary>
public sealed class ItunesMetadataPlugin(IMetadataCache cache, IMetadataTrace trace) : IMetadataPlugin
{
    public string Id => "itunes";
    public string DisplayName => "Apple Music (iTunes)";
    public int SortOrder => 20;
    public IReadOnlyList<IMetadataProviderFactory> Providers { get; } = [new ItunesProviderFactory(cache, trace)];
}
