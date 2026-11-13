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
        // The account, the players and what goes first: one each, shared by both sources and by
        // the Spotify metadata plugin (which reads the catalog through this connection).
        services.AddSingleton<SpotifySettings>();
        services.AddSingleton(sp => new SpotifyAuthStore(sp.GetRequiredService<IAppFolders>().Data("spotify-auth.dat"),
            sp.GetRequiredService<ISecretProtector>()));
        services.AddSingleton(sp => new SpotifyConnection(
            sp.GetRequiredService<SpotifyAuthStore>(), sp.GetRequiredService<SpotifySettings>().ClientId,
            sp.GetRequiredService<IPluginLog>()));
        services.AddSingleton(sp => new SpotifyPlaybackService(
            sp.GetRequiredService<SpotifyConnection>(), sp.GetRequiredService<SpotifySettings>().Librespot,
            sp.GetRequiredService<IProcesses>(), sp.GetRequiredService<IPluginLog>()));
        // The tracks Spotify refuses librespot play through Soloist, once the user has set it up.
        services.AddSingleton(sp =>
        {
            var settings = sp.GetRequiredService<SpotifySettings>();
            var soloist = new SoloistPlaybackService(settings.Soloist, settings.SoloistDeviceName,
                sp.GetRequiredService<IProcesses>(), sp.GetRequiredService<IPluginLog>());
            // A track Soloist plays waits for the user to pair Soloist once; only they can (in the
            // Spotify app), so say how.
            var notifications = sp.GetRequiredService<INotifications>();
            soloist.PairingNeeded += device => notifications.Show("Pair Spotify Soloist",
                "Spotify won't play this track through librespot. In the Spotify app, " +
                $"pick the device \"{device}\" once; the track plays when you have.",
                NotificationKind.Info, TimeSpan.FromSeconds(30));
            return soloist;
        });
        services.AddSingleton(sp => new SpotifyPlayerChoice(sp.GetRequiredService<IAppFolders>().Data("spotify-player.json")));

        services.AddSingleton<IAudioSourceFactory, SpotifySourceFactory>();
        services.AddSingleton<IAudioSourceFactory, SpotifyContentSourceFactory>();
    }
}
