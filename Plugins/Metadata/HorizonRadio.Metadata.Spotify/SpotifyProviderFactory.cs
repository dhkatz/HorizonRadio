using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using HorizonRadio.Sdk;
using HorizonRadio.Sdk.Metadata;
using HorizonRadio.Sdk.Sources.Config;
using HorizonRadio.Source.Spotify;
using SpotifyAPI.Web;

namespace HorizonRadio.Metadata.Spotify;

public sealed class SpotifyProviderFactory(IMetadataCache cache, IMetadataTrace trace) : IMetadataProviderFactory
{
    public const string KeyClientId = "clientId";
    public const string KeyClientSecret = "clientSecret";

    public string Id => "spotify";
    public string DisplayName => "Spotify";
    public string? Description => "Spotify Web API — best for modern releases. By default it reuses your connected Spotify source (Sources tab), so no extra setup is needed. Optionally enter a separate app's Client ID + Secret to look up metadata independently of the source.";

    public IReadOnlyList<ConfigField> Schema { get; } =
    [
        new TextField(
            Key:         KeyClientId,
            Label:       "Client ID (optional)",
            Default:     "",
            Placeholder: "leave blank to reuse the connected Spotify source",
            Description: "Only needed to use a separate app. Leave blank to ride on the Spotify source you connect in the Sources tab."),

        new TextField(
            Key:         KeyClientSecret,
            Label:       "Client Secret (optional)",
            Default:     "",
            Placeholder: "required only if Client ID is set",
            IsSecret:    true,
            Description: "Pairs with the Client ID above (client-credentials flow). Stored encrypted for your Windows user, and never synced to your other devices."),
    ];

    public IMetadataProvider Create(ConfigValues values)
    {
        var id = values.GetString(KeyClientId);
        var secret = values.GetString(KeyClientSecret);

        if (!string.IsNullOrWhiteSpace(id) && !string.IsNullOrWhiteSpace(secret))
        {
            // Independent app-level credentials (client-credentials flow). Built once; the
            // authenticator refreshes its app token internally.
            var client = new SpotifyClient(SpotifyClientConfig.CreateDefault()
                .WithAuthenticator(new ClientCredentialsAuthenticator(id!, secret!)));
            return new SpotifyProvider(cache, trace, _ => Task.FromResult<SpotifyClient?>(client));
        }

        // No separate credentials: ride on the connected Spotify source so the user needn't
        // register a second app. Read the connection lazily (it may initialize after the
        // pipeline is first built) and no-op until the source is connected.
        return new SpotifyProvider(cache, trace, ct =>
            SpotifyRuntime.Connection?.GetClientAsync(ct) ?? Task.FromResult<SpotifyClient?>(null));
    }
}
