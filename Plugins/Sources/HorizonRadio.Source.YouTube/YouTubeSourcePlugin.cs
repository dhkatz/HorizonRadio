using HorizonRadio.Sdk;
using HorizonRadio.Sdk.Sources;
using Microsoft.Extensions.DependencyInjection;

namespace HorizonRadio.Source.YouTube;

/// <summary>The YouTube source plugin.</summary>
public sealed class YouTubeSourcePlugin : IPlugin, IPluginModule
{
    public string Id => "youtube";
    public string DisplayName => "YouTube";
    public int SortOrder => 30;

    public void ConfigureServices(IServiceCollection services)
    {
        services.AddSingleton<IAudioSourceFactory, YouTubeSourceFactory>();
    }
}
