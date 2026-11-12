using System.Collections.Generic;
using HorizonRadio.Sdk;
using HorizonRadio.Sdk.Metadata;

namespace HorizonRadio.Metadata.MusicBrainz;

/// <summary>The MusicBrainz metadata plugin — contributes the MusicBrainz provider.</summary>
public sealed class MusicBrainzMetadataPlugin : IMetadataPlugin
{
    public string Id => "musicbrainz";
    public string DisplayName => "MusicBrainz";
    public int SortOrder => 30;
    public IReadOnlyList<IMetadataProviderFactory> Providers { get; } = [new MusicBrainzProviderFactory()];
}
