using HorizonRadio.Sdk;
using HorizonRadio.Sdk.Sources;
using Microsoft.Extensions.DependencyInjection;

namespace HorizonRadio.Source.Radio;

/// <summary>The internet-radio source plugin.</summary>
public sealed class RadioSourcePlugin : IPlugin, IPluginModule
{
    public string Id => "radio";
    public string DisplayName => "Internet Radio";
    public int SortOrder => 40;

    public void ConfigureServices(IServiceCollection services)
    {
        services.AddSingleton<RadioBrowserClient>();
        services.AddSingleton<IAudioSourceFactory, RadioSourceFactory>();
    }
}
