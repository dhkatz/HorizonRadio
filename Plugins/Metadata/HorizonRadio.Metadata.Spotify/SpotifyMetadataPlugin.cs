using System.Collections.Generic;
using HorizonRadio.Sdk;
using HorizonRadio.Sdk.Metadata;

namespace HorizonRadio.Metadata.Spotify;

/// <summary>The Spotify metadata plugin — contributes the Spotify Web API provider.</summary>
public sealed class SpotifyMetadataPlugin : IMetadataPlugin
{
    public string Id => "spotify";
    public string DisplayName => "Spotify";
    public int SortOrder => 10;
    public IReadOnlyList<IMetadataProviderFactory> Providers { get; } = [new SpotifyProviderFactory()];
}
