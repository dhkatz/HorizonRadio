using System;
using System.Collections.Generic;
using HorizonRadio.Core.Sources.Config;

namespace HorizonRadio.Core.Metadata;

/// <summary>
/// Factory for <see cref="SpotifyEnricher"/>. The user supplies a
/// client_id + client_secret pair created at developer.spotify.com.
/// Client Credentials flow is read-only and doesn't need a redirect
/// URL or user login.
/// </summary>
public sealed class SpotifyEnricherFactory : IMetadataProviderFactory
{
    public const string KeyClientId     = "clientId";
    public const string KeyClientSecret = "clientSecret";

    public string  Id          => "spotify";
    public string  DisplayName => "Spotify";
    public string? Description => "Spotify Web API. Best results for Spotify Connect tracks and most modern releases. Requires a Client ID + Secret from developer.spotify.com (create a free app, no redirect URL needed).";

    public IReadOnlyList<ConfigField> Schema { get; } = new ConfigField[]
    {
        new TextField(
            Key:         KeyClientId,
            Label:       "Client ID",
            Default:     "",
            Placeholder: "32-character hex string",
            Description: "From your Spotify developer dashboard."),

        new TextField(
            Key:         KeyClientSecret,
            Label:       "Client Secret",
            Default:     "",
            Placeholder: "32-character hex string",
            IsSecret:    true,
            Description: "From your Spotify developer dashboard. Stored in your local config file unencrypted; treat the file like any other credential store."),
    };

    public IMetadataEnricher Create(ConfigValues values, MetadataCache cache)
    {
        var id     = values.GetString(KeyClientId);
        var secret = values.GetString(KeyClientSecret);
        if (string.IsNullOrWhiteSpace(id) || string.IsNullOrWhiteSpace(secret))
            throw new InvalidOperationException(
                "Spotify: provide Client ID and Client Secret (developer.spotify.com).");
        return new SpotifyEnricher(cache, id!, secret!);
    }
}
