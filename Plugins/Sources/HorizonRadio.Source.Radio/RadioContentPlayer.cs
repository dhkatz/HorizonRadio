using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using HorizonRadio.Sdk.Sources;

namespace HorizonRadio.Source.Radio;

/// <summary>
/// Content-free internet-radio engine: holds the ffmpeg path and resolves a
/// <see cref="ContentRef"/> locator into a playable station. Two locator forms:
/// <c>radio://{uuid}</c> (a directory hit from search — resolved against
/// <see cref="RadioBrowserClient"/> for the station name, logo, and a resolved stream
/// URL) and a plain <c>http(s)://</c> URL (the paste-a-URL escape hatch, whose name
/// fills in from the stream's <c>icy-name</c> at connect time).
///
/// A station is a single infinite stream, so <see cref="EnumerateAsync"/> always yields
/// exactly one <see cref="RadioPlayableItem"/>.
/// </summary>
public sealed class RadioContentPlayer(string ffmpegPath, RadioBrowserClient directory)
    : IContentPlayer, IContentDescriber
{
    public async Task<IReadOnlyList<PlayableItem>> EnumerateAsync(ContentRef content, CancellationToken ct)
    {
        var station = await ResolveStationAsync(content.Locator, directory, ct).ConfigureAwait(false);
        return [new RadioPlayableItem(station, ffmpegPath)];
    }

    /// <summary>The station's name, where it broadcasts from, and its logo. A pasted stream URL
    /// has only its address until it's tuned in.</summary>
    public async Task<ContentSummary?> DescribeAsync(ContentRef content, CancellationToken ct)
    {
        var station = await ResolveStationAsync(content.Locator, directory, ct).ConfigureAwait(false);
        var subtitle = string.Join(" · ", new[] { station.Country, FirstTags(station.Tags) }
            .Where(s => !string.IsNullOrWhiteSpace(s)));
        return new ContentSummary(
            Title: station.Name,
            Subtitle: subtitle.Length > 0 ? subtitle : "Live radio",
            Kind: ContentKind.Station,
            ArtUrl: string.IsNullOrWhiteSpace(station.FaviconUrl) ? null : station.FaviconUrl);
    }

    // radio-browser tags are a long comma list; a few read as a genre line.
    private static string? FirstTags(string? tags) =>
        string.IsNullOrWhiteSpace(tags)
            ? null
            : string.Join(", ", tags.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Take(3));

    private static async Task<RadioStation> ResolveStationAsync(string? locator, RadioBrowserClient directory, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(locator))
            throw new InvalidOperationException("Internet Radio: enter a station URL.");
        locator = locator.Trim();

        if (locator.StartsWith("radio://", StringComparison.OrdinalIgnoreCase))
        {
            var uuid = locator["radio://".Length..];
            var station = await directory.ResolveAsync(uuid, ct).ConfigureAwait(false);
            return station
                ?? throw new InvalidOperationException("Internet Radio: that station couldn't be found.");
        }

        if (!locator.StartsWith("http://", StringComparison.OrdinalIgnoreCase) &&
            !locator.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Internet Radio: paste an http(s) stream URL, or pick a station from search.");

        // Direct stream. Use the URL as a placeholder name; icy-name refines it on connect.
        return new RadioStation(Uuid: "", Name: locator, StreamUrl: locator);
    }
}
