using HorizonRadio.Sdk;
using HorizonRadio.Sdk.Sources;
using Microsoft.Extensions.DependencyInjection;

namespace HorizonRadio.Source.Local;

/// <summary>The local-files source plugin.</summary>
public sealed class LocalSourcePlugin : IPlugin, IPluginModule
{
    public string Id => "local";
    public string DisplayName => "Local Files";
    public int SortOrder => 10;

    public void ConfigureServices(IServiceCollection services)
    {
        services.AddSingleton<IAudioSourceFactory, LocalFileSourceFactory>();
    }
}
