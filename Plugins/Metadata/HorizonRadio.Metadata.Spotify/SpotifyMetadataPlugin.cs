using HorizonRadio.Sdk;
using HorizonRadio.Sdk.Metadata;
using Microsoft.Extensions.DependencyInjection;

namespace HorizonRadio.Metadata.Spotify;

/// <summary>The Spotify metadata plugin — contributes the Spotify Web API provider.</summary>
public sealed class SpotifyMetadataPlugin : IPlugin, IPluginModule
{
    public string Id => "spotify";
    public string DisplayName => "Spotify";
    public int SortOrder => 10;

    public void ConfigureServices(IServiceCollection services)
    {
        services.AddSingleton<IMetadataProviderFactory, SpotifyProviderFactory>();
    }
}
