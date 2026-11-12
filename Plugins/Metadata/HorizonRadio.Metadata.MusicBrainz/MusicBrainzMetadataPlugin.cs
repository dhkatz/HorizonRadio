using HorizonRadio.Sdk;
using HorizonRadio.Sdk.Metadata;
using Microsoft.Extensions.DependencyInjection;

namespace HorizonRadio.Metadata.MusicBrainz;

/// <summary>The MusicBrainz metadata plugin — contributes the MusicBrainz provider.</summary>
public sealed class MusicBrainzMetadataPlugin : IPlugin, IPluginModule
{
    public string Id => "musicbrainz";
    public string DisplayName => "MusicBrainz";
    public int SortOrder => 30;

    public void ConfigureServices(IServiceCollection services)
    {
        services.AddSingleton<IMetadataProviderFactory, MusicBrainzProviderFactory>();
    }
}
