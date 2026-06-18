using System.Collections.Generic;
using HorizonRadio.Plugins.Abstractions;

namespace HorizonRadio.Core.Metadata.VocaDb;

/// <summary>The VocaDB metadata plugin — contributes the VocaDB provider.</summary>
public sealed class VocaDbMetadataPlugin : IMetadataPlugin
{
    public string Id => "vocadb";
    public string DisplayName => "VocaDB";
    public IReadOnlyList<IMetadataProviderFactory> Providers { get; } = [new VocaDbProviderFactory()];
}
