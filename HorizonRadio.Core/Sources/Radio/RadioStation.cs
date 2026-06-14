namespace HorizonRadio.Core.Sources.Radio;

/// <summary>
/// One internet-radio station as returned by the radio-browser directory. Only the
/// fields the source actually uses are kept; <see cref="StreamUrl"/> is the resolved,
/// playable URL (radio-browser's <c>url_resolved</c>, which follows .pls/.m3u redirects),
/// and <see cref="FaviconUrl"/> is the station logo used as fallback art until a song
/// title arrives and the metadata pipeline supplies square cover art.
/// </summary>
public sealed record RadioStation(
    string Uuid,
    string Name,
    string StreamUrl,
    string? Homepage = null,
    string? FaviconUrl = null,
    string? Codec = null,
    int? Bitrate = null,
    string? Country = null,
    string? Tags = null);
