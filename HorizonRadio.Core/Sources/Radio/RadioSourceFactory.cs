using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using HorizonRadio.Core.Sources.Config;
using HorizonRadio.Core.Tools;

namespace HorizonRadio.Core.Sources.Radio;

/// <summary>
/// Factory for the Internet Radio source. It's content-addressable (a station URL is
/// the locator, decoded by ffmpeg) and searchable (the radio-browser directory). The
/// content field accepts either a pasted <c>http(s)</c> stream URL or a
/// <c>radio://{uuid}</c> locator that search produces; only ffmpeg is required (the
/// directory is keyless HTTP, so search works before any tool is installed).
/// </summary>
public sealed class RadioSourceFactory : IContentSourceFactory, ISearchSource
{
    /// <summary>Catalog id search results carry so the enqueuer can find this factory
    /// again (see <see cref="SourceCatalog.Find"/>).</summary>
    public const string SourceId = "radio";

    public const string KeyUrl = "url";
    public const string KeyFfmpeg = "ffmpeg";

    public string Id => SourceId;
    public string DisplayName => "Internet Radio";
    public string? Description => "Stream and search live internet radio stations (radio-browser.info), with live now-playing.";

    public IReadOnlyList<ConfigField> Schema { get; }

    public RadioSourceFactory()
    {
        Schema =
        [
            new TextField(
                Key:         KeyUrl,
                Label:       "Station stream URL",
                Placeholder: "https://…/stream.mp3 — or just search for a station",
                Description: "A direct stream URL. You usually don't need this: search a station name in the search box instead."),

            new ToolField(
                Key:         KeyFfmpeg,
                Label:       "ffmpeg.exe",
                ToolKind:    ToolKind.Ffmpeg,
                Description: "Install via the Tools tab, or point at an existing ffmpeg.exe."),
        ];
    }

    public string ContentKey => KeyUrl;
    public string LocatorHint => "https://…/stream.mp3 (or pick a station from search)";

    public IContentPlayer CreatePlayer(ConfigValues values)
    {
        var ffmpeg = ToolResolver.Resolve(values.GetString(KeyFfmpeg), ToolKind.Ffmpeg);
        if (ffmpeg is null)
            throw new InvalidOperationException("Internet Radio: pick an ffmpeg.exe path.");

        return new RadioContentPlayer(ffmpeg, RadioBrowserClient.Shared);
    }

    public IAudioSource Create(ConfigValues values)
        => CreatePlayer(values).Open(new ContentRef(Id, values.GetString(ContentKey) ?? ""));

    // -- ISearchSource (radio-browser directory → radio://uuid locators) --

    public async Task<IReadOnlyList<SearchResult>> SearchAsync(string query, int limit, CancellationToken ct = default)
    {
        // Pure HTTP, no tool needed — and never throws, so a directory outage can't
        // break a query that spans other sources.
        var stations = await RadioBrowserClient.Shared.SearchAsync(query, limit, ct).ConfigureAwait(false);
        return [.. stations.Select(ToResult)];
    }

    private static SearchResult ToResult(RadioStation s) => new(
        SourceId: SourceId,
        Kind: SearchResultKind.Track,
        Title: s.Name,
        Subtitle: BuildSubtitle(s),
        ArtUrl: s.FaviconUrl,
        Locator: $"radio://{s.Uuid}",
        Duration: null);

    private static string BuildSubtitle(RadioStation s)
    {
        var parts = new List<string>(3);
        if (!string.IsNullOrWhiteSpace(s.Tags))
        {
            var tags = s.Tags!.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                              .Take(2);
            parts.Add(string.Join(", ", tags));
        }
        if (!string.IsNullOrWhiteSpace(s.Country)) parts.Add(s.Country!);
        if (s.Bitrate is > 0) parts.Add($"{s.Bitrate} kbps");
        return parts.Count > 0 ? string.Join(" · ", parts) : "Internet radio station";
    }
}
