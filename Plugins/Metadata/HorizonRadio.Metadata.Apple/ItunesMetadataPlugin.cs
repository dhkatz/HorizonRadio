using HorizonRadio.Sdk;
using HorizonRadio.Sdk.Metadata;
using Microsoft.Extensions.DependencyInjection;

namespace HorizonRadio.Metadata.Apple;

/// <summary>The Apple/iTunes metadata plugin — contributes the iTunes provider.</summary>
public sealed class ItunesMetadataPlugin : IPlugin, IPluginModule
{
    public string Id => "itunes";
    public string DisplayName => "Apple Music (iTunes)";
    public int SortOrder => 20;

    public void ConfigureServices(IServiceCollection services)
    {
        services.AddSingleton<IMetadataProviderFactory, ItunesProviderFactory>();
    }
}
