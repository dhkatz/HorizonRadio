using System;
using System.Collections.Generic;
using HorizonRadio.Core.Sources.Config;

namespace HorizonRadio.Core.Sources.InternetRadio;

/// <summary>
/// Configuration bag for <see cref="InternetRadioSource"/>. Constructed by
/// <see cref="InternetRadioSourceFactory"/> from the user's UI input.
/// </summary>
public sealed class InternetRadioOptions
{
    /// <summary>
    /// Direct stream URL. Must be an HTTP/HTTPS URL pointing to an
    /// MP3 or OGG/Opus audio stream, e.g.
    /// <c>https://radio.supitszaire.com/listen/melon-cafe-fm/radio.mp3</c>
    /// </summary>
    public required string StreamUrl { get; init; }

    /// <summary>
    /// Optional URL for track metadata. Leave empty to rely solely on
    /// ICY in-stream metadata.
    ///
    /// Supported formats:
    /// <list type="bullet">
    ///   <item>AzuraCast SSE: <c>https://…/api/live/nowplaying/sse?cf_connect=…</c></item>
    ///   <item>AzuraCast REST: <c>https://…/api/nowplaying/&lt;station&gt;</c></item>
    /// </list>
    /// </summary>
    public string? MetadataUrl { get; init; }

    /// <summary>
    /// Human-readable station name shown in the HUD and UI while the stream
    /// is connecting or when metadata is unavailable.
    /// </summary>
    public string StationName { get; init; } = "Internet Radio";
}

/// <summary>
/// Factory that registers the Internet Radio source in the
/// <see cref="SourceCatalog"/> and turns user-supplied config values into
/// a running <see cref="InternetRadioSource"/>.
///
/// Config fields exposed in the UI:
/// <list type="bullet">
///   <item><b>streamUrl</b> — direct MP3 / OGG stream URL (required)</item>
///   <item><b>metadataUrl</b> — AzuraCast SSE or REST metadata URL (optional)</item>
///   <item><b>stationName</b> — display name shown in the game HUD (optional)</item>
/// </list>
/// </summary>
public sealed class InternetRadioSourceFactory : IAudioSourceFactory
{
    public const string KeyStreamUrl   = "streamUrl";
    public const string KeyMetadataUrl = "metadataUrl";
    public const string KeyStationName = "stationName";

    public string  Id          => "internet-radio";
    public string  DisplayName => "Internet Radio";
    public string? Description => "Stream any MP3 or Ogg internet radio URL. Supports AzuraCast metadata (SSE/REST) and ICY in-stream metadata.";

    public IReadOnlyList<ConfigField> Schema { get; } = new ConfigField[]
    {
        new TextField(
            Key:         KeyStreamUrl,
            Label:       "Stream URL",
            Default:     "",
            Placeholder: "https://radio.example.com/stream.mp3",
            Description: "Direct HTTP/HTTPS URL to an MP3 or Ogg/Opus audio stream."),

        new TextField(
            Key:         KeyMetadataUrl,
            Label:       "Metadata URL (optional)",
            Default:     "",
            Placeholder: "https://radio.example.com/api/nowplaying/station",
            Description: "AzuraCast SSE (/api/live/nowplaying/sse?cf_connect=…) or REST (/api/nowplaying/<station>) endpoint. Leave blank to use ICY in-stream metadata only."),

        new TextField(
            Key:         KeyStationName,
            Label:       "Station name",
            Default:     "Internet Radio",
            Placeholder: "Internet Radio",
            Description: "Shown in the game HUD while connecting or when no track metadata is available."),
    };

    public IAudioSource Create(ConfigValues values)
    {
        var streamUrl = values.GetString(KeyStreamUrl)?.Trim();
        if (string.IsNullOrEmpty(streamUrl))
            throw new InvalidOperationException("Internet Radio: enter a stream URL.");

        if (!Uri.TryCreate(streamUrl, UriKind.Absolute, out var uri) ||
            (uri.Scheme != "http" && uri.Scheme != "https"))
            throw new InvalidOperationException("Internet Radio: stream URL must be an http:// or https:// address.");

        var metaUrl     = values.GetString(KeyMetadataUrl)?.Trim();
        var stationName = values.GetString(KeyStationName)?.Trim();
        if (string.IsNullOrEmpty(stationName)) stationName = "Internet Radio";

        return new InternetRadioSource(new InternetRadioOptions
        {
            StreamUrl   = streamUrl,
            MetadataUrl = string.IsNullOrEmpty(metaUrl) ? null : metaUrl,
            StationName = stationName,
        });
    }
}
