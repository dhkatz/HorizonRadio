using System.Collections.Generic;
using HorizonRadio.Plugins.Abstractions;

namespace HorizonRadio.Core.Metadata.Apple;

/// <summary>The Apple/iTunes metadata plugin — contributes the iTunes provider.</summary>
public sealed class ItunesMetadataPlugin : IMetadataPlugin
{
    public string Id => "itunes";
    public string DisplayName => "Apple Music (iTunes)";
    public int SortOrder => 20;
    public IReadOnlyList<IMetadataProviderFactory> Providers { get; } = [new ItunesProviderFactory()];
}
