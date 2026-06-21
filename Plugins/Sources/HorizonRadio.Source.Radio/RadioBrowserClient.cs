using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;

namespace HorizonRadio.Core.Sources.Radio;

/// <summary>
/// Thin client for the community radio-browser.info directory — the searchable station
/// catalog behind the Internet Radio source. radio-browser has no API key; it asks only
/// for a descriptive <c>User-Agent</c> and that clients spread load across its mirrors.
///
/// Mirrors are discovered at runtime (the directory is volunteer-run, so a fixed host
/// goes stale): resolve the round-robin name <c>all.api.radio-browser.info</c> to its
/// member IPs, reverse-DNS each to a real mirror host, and try them in turn with
/// failover. The directory specifics (endpoints, JSON shape) stay encapsulated here so
/// the factory/player only ever deal in <see cref="RadioStation"/>.
/// </summary>
public sealed class RadioBrowserClient : IDisposable
{
    /// <summary>Shared instance for the parameterless factory in <see cref="SourceCatalog"/>
    /// (which has no DI seam) and the content player's uuid resolve.</summary>
    public static RadioBrowserClient Shared { get; } = new();

    private const string DnsSeed = "all.api.radio-browser.info";

    // Ultimate fallbacks if DNS discovery yields nothing. The round-robin name itself
    // always answers, so it's a safe (if less polite) last resort.
    private static readonly string[] FallbackMirrors =
    [
        "https://de1.api.radio-browser.info",
        "https://nl1.api.radio-browser.info",
        "https://at1.api.radio-browser.info",
        "https://all.api.radio-browser.info",
    ];

    private readonly HttpClient _http;
    private readonly SemaphoreSlim _mirrorGate = new(1, 1);
    private IReadOnlyList<string>? _mirrors;

    public RadioBrowserClient()
    {
        _http = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };
        // radio-browser blocks empty/blank User-Agents; identify ourselves.
        _http.DefaultRequestHeaders.UserAgent.ParseAdd("HorizonRadio/0.5 (internet-radio)");
    }

    /// <summary>Search stations by name. Hides broken streams and orders by popularity
    /// (click count) so the strongest matches surface first. Never throws — returns an
    /// empty list on any failure so a directory outage can't break a multi-source query.</summary>
    public async Task<IReadOnlyList<RadioStation>> SearchAsync(string query, int limit, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(query)) return [];
        var q = Uri.EscapeDataString(query.Trim());
        var path = $"/json/stations/search?name={q}&limit={limit}&hidebroken=true&order=clickcount&reverse=true";
        var dtos = await GetAsync<List<StationDto>>(path, ct).ConfigureAwait(false);
        return dtos is null ? [] : [.. dtos.Select(Map).Where(s => s is not null)!];
    }

    /// <summary>Resolve a single station by its stable uuid (the form search results carry
    /// in their locator). Returns null if it can't be found.</summary>
    public async Task<RadioStation?> ResolveAsync(string uuid, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(uuid)) return null;
        var dtos = await GetAsync<List<StationDto>>(
            $"/json/stations/byuuid/{Uri.EscapeDataString(uuid)}", ct).ConfigureAwait(false);
        return dtos is { Count: > 0 } ? Map(dtos[0]) : null;
    }

    private static RadioStation? Map(StationDto d)
    {
        // url_resolved follows .pls/.m3u redirects to a direct stream; fall back to url.
        var stream = !string.IsNullOrWhiteSpace(d.UrlResolved) ? d.UrlResolved : d.Url;
        if (string.IsNullOrWhiteSpace(stream) || string.IsNullOrWhiteSpace(d.Name)) return null;
        return new RadioStation(
            Uuid: d.StationUuid ?? "",
            Name: d.Name!.Trim(),
            StreamUrl: stream!,
            Homepage: NullIfBlank(d.Homepage),
            FaviconUrl: NullIfBlank(d.Favicon),
            Codec: NullIfBlank(d.Codec),
            Bitrate: d.Bitrate > 0 ? d.Bitrate : null,
            Country: NullIfBlank(d.Country),
            Tags: NullIfBlank(d.Tags));
    }

    private static string? NullIfBlank(string? s) => string.IsNullOrWhiteSpace(s) ? null : s.Trim();

    // -- HTTP with mirror failover --

    private async Task<T?> GetAsync<T>(string path, CancellationToken ct) where T : class
    {
        var mirrors = await GetMirrorsAsync(ct).ConfigureAwait(false);
        for (int i = 0; i < mirrors.Count; i++)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                return await _http.GetFromJsonAsync<T>(mirrors[i] + path, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
            catch (Exception ex)
            {
                Diagnostics.ProcessConsole.Append("radio", $"mirror {mirrors[i]} failed: {ex.Message}");
                // Try the next mirror.
            }
        }
        return null;
    }

    private async Task<IReadOnlyList<string>> GetMirrorsAsync(CancellationToken ct)
    {
        if (_mirrors is { Count: > 0 }) return _mirrors;
        await _mirrorGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (_mirrors is { Count: > 0 }) return _mirrors;
            _mirrors = await DiscoverMirrorsAsync(ct).ConfigureAwait(false);
            return _mirrors;
        }
        finally { _mirrorGate.Release(); }
    }

    private static async Task<IReadOnlyList<string>> DiscoverMirrorsAsync(CancellationToken ct)
    {
        var hosts = new List<string>();
        try
        {
            var addrs = await System.Net.Dns.GetHostAddressesAsync(DnsSeed, ct).ConfigureAwait(false);
            foreach (var addr in addrs)
            {
                try
                {
                    var entry = await System.Net.Dns.GetHostEntryAsync(addr).ConfigureAwait(false);
                    var url = "https://" + entry.HostName;
                    if (!hosts.Contains(url)) hosts.Add(url);
                }
                catch { /* reverse DNS can fail per-IP; skip it */ }
            }
        }
        catch (Exception ex)
        {
            Diagnostics.ProcessConsole.Append("radio", $"mirror discovery failed: {ex.Message}");
        }

        return hosts.Count > 0 ? hosts : FallbackMirrors;
    }

    public void Dispose()
    {
        _http.Dispose();
        _mirrorGate.Dispose();
    }

    private sealed class StationDto
    {
        [JsonPropertyName("stationuuid")] public string? StationUuid { get; set; }
        [JsonPropertyName("name")] public string? Name { get; set; }
        [JsonPropertyName("url")] public string? Url { get; set; }
        [JsonPropertyName("url_resolved")] public string? UrlResolved { get; set; }
        [JsonPropertyName("homepage")] public string? Homepage { get; set; }
        [JsonPropertyName("favicon")] public string? Favicon { get; set; }
        [JsonPropertyName("tags")] public string? Tags { get; set; }
        [JsonPropertyName("country")] public string? Country { get; set; }
        [JsonPropertyName("codec")] public string? Codec { get; set; }
        [JsonPropertyName("bitrate")] public int Bitrate { get; set; }
    }
}
