using HorizonRadio.Sdk;
using HorizonRadio.Sdk.Sources;
using Microsoft.Extensions.DependencyInjection;

namespace HorizonRadio.Source.Test;

/// <summary>The diagnostic test-tone source plugin.</summary>
public sealed class TestToneSourcePlugin : IPlugin, IPluginModule
{
    public string Id => "testtone";
    public string DisplayName => "Test Tone";
    public int SortOrder => 50;

    public void ConfigureServices(IServiceCollection services)
    {
        services.AddSingleton<IAudioSourceFactory, TestToneSourceFactory>();
    }
}
