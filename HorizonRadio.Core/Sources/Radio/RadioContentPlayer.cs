using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace HorizonRadio.Core.Sources.Radio;

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
public sealed class RadioContentPlayer(string ffmpegPath, RadioBrowserClient directory) : IContentPlayer
{
    // Hand the source the locator (not a resolved station): a radio:// locator needs an
    // async directory lookup, which RadioSource does inside its own run loop — never block
    // the caller's thread.
    public IAudioSource Open(ContentRef content) => new RadioSource(content.Locator, ffmpegPath, directory);

    public async Task<IReadOnlyList<PlayableItem>> EnumerateAsync(ContentRef content, CancellationToken ct)
    {
        var station = await ResolveStationAsync(content.Locator, directory, ct).ConfigureAwait(false);
        return [new RadioPlayableItem(station, ffmpegPath)];
    }

    internal static async Task<RadioStation> ResolveStationAsync(string? locator, RadioBrowserClient directory, CancellationToken ct)
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
