using HorizonRadio.Sdk;
using HorizonRadio.Sdk.Sources;
using Microsoft.Extensions.DependencyInjection;

namespace HorizonRadio.Source.Spotify;

/// <summary>
/// The Spotify source plugin. Ships two factories by design: the zero-config Connect receiver
/// (<see cref="SpotifySourceFactory"/>) and the driven, mixable source
/// (<see cref="SpotifyContentSourceFactory"/>) — both drive one librespot device.
/// </summary>
public sealed class SpotifySourcePlugin : IPlugin, IPluginModule
{
    public string Id => "spotify";
    public string DisplayName => "Spotify";
    public int SortOrder => 20;

    public void ConfigureServices(IServiceCollection services)
    {
        services.AddSingleton<IAudioSourceFactory, SpotifySourceFactory>();
        services.AddSingleton<IAudioSourceFactory, SpotifyContentSourceFactory>();
    }
}
