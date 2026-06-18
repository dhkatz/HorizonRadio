using System.Collections.Generic;
using HorizonRadio.Plugins.Abstractions;

namespace HorizonRadio.Core.Metadata.Spotify;

/// <summary>The Spotify metadata plugin — contributes the Spotify Web API provider.</summary>
public sealed class SpotifyMetadataPlugin : IMetadataPlugin
{
    public string Id => "spotify";
    public string DisplayName => "Spotify";
    public IReadOnlyList<IMetadataProviderFactory> Providers { get; } = [new SpotifyProviderFactory()];
}
