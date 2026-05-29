using System;
using System.Diagnostics;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using HorizonRadio.Core.Models;

namespace HorizonRadio.Core.Sources.InternetRadio;

/// <summary>
/// Polls metadata for an internet radio stream using one of two strategies:
///
/// <list type="bullet">
///   <item><b>AzuraCast SSE</b> — if a metadata URL is supplied and ends with
///   <c>/sse</c> (or contains <c>/api/live/nowplaying/sse</c>), opens a
///   Server-Sent Events stream and parses the Centrifugo envelope that
///   AzuraCast emits. This is what melon-radio uses:
///   <c>https://radio.supitszaire.com/api/live/nowplaying/sse?cf_connect=…</c></item>
///
///   <item><b>AzuraCast REST polling</b> — if the metadata URL looks like an
///   AzuraCast <c>/api/nowplaying/&lt;station&gt;</c> endpoint, polls every
///   ~10 s and parses the JSON response.</item>
///
///   <item><b>No external metadata</b> — falls back to ICY metadata embedded in
///   the stream itself (the source layer handles this via the <c>icy-metaint</c>
///   HTTP response header).</item>
/// </list>
///
/// Raises <see cref="TrackChanged"/> whenever the track changes. The caller
/// is responsible for wiring this up to its own <c>TrackChanged</c> event.
/// </summary>
internal sealed class InternetRadioMetadataPoller : IAsyncDisposable
{
    public event Action<string, string, string?>? TrackChanged; // title, artist, artUrl

    private readonly string?    _metadataUrl;
    private readonly string     _sourceId;
    private readonly string     _sourceDisplay;

    private CancellationTokenSource? _cts;
    private Task?                    _pollTask;

    private static readonly HttpClient _http = new(new HttpClientHandler
    {
        AllowAutoRedirect = true,
    })
    {
        Timeout = TimeSpan.FromSeconds(30),
    };

    private static void Log(string msg) => Debug.WriteLine($"[hzn-radio-meta] {msg}");

    public InternetRadioMetadataPoller(string? metadataUrl, string sourceId, string sourceDisplay)
    {
        _metadataUrl   = metadataUrl;
        _sourceId      = sourceId;
        _sourceDisplay = sourceDisplay;
    }

    public void Start(CancellationToken externalCt)
    {
        if (string.IsNullOrWhiteSpace(_metadataUrl)) return;
        _cts      = CancellationTokenSource.CreateLinkedTokenSource(externalCt);
        _pollTask = Task.Run(() => RunAsync(_cts.Token));
    }

    public async ValueTask DisposeAsync()
    {
        _cts?.Cancel();
        if (_pollTask != null)
        {
            try { await _pollTask.ConfigureAwait(false); }
            catch { }
        }
        _cts?.Dispose();
    }

    // -----------------------------------------------------------------------

    private async Task RunAsync(CancellationToken ct)
    {
        var url = _metadataUrl!;
        try
        {
            if (IsAzuracastSse(url))
                await RunAzuracastSseAsync(url, ct).ConfigureAwait(false);
            else
                await RunRestPollingAsync(url, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            Log($"metadata poller crashed: {ex.GetType().Name}: {ex.Message}");
        }
    }

    // -----------------------------------------------------------------------
    // AzuraCast SSE (Centrifugo envelope)
    // -----------------------------------------------------------------------

    private static bool IsAzuracastSse(string url) =>
        url.Contains("/sse", StringComparison.OrdinalIgnoreCase) ||
        url.Contains("cf_connect", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Connects to the AzuraCast SSE endpoint and parses Centrifugo messages.
    ///
    /// melon-radio uses:
    ///   GET /api/live/nowplaying/sse?cf_connect={"subs":{"station:melon-cafe-fm":{"recover":true}}}
    ///
    /// The first message has shape:
    ///   {"connect":{"subs":{"station:X":{"publications":[{"data":{...}}]}}}}
    ///
    /// Subsequent messages:
    ///   {"channel":"station:X","pub":{"data":{...}}}
    ///
    /// The inner "data" object has:
    ///   data.np.now_playing.song  → { title, artist, art }
    /// </summary>
    private async Task RunAzuracastSseAsync(string url, CancellationToken ct)
    {
        Log($"SSE connect: {url}");

        // Build the cf_connect param if it isn't already in the URL.
        // If the user just provided the bare /api/live/nowplaying/sse URL
        // without query params, we add a generic subscription that AzuraCast
        // will auto-route to the default station's channel.
        string sseUrl = url;
        if (!sseUrl.Contains("cf_connect", StringComparison.OrdinalIgnoreCase))
        {
            // Subscribe to the wildcard channel; AzuraCast will push whatever
            // station the endpoint is for.
            var sub = Uri.EscapeDataString("{\"subs\":{\"station:main\":{\"recover\":true}}}");
            sseUrl += (sseUrl.Contains('?') ? "&" : "?") + "cf_connect=" + sub;
        }

        while (!ct.IsCancellationRequested)
        {
            try
            {
                using var req = new HttpRequestMessage(HttpMethod.Get, sseUrl);
                req.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("text/event-stream"));
                req.Headers.CacheControl = new CacheControlHeaderValue { NoCache = true };

                using var resp = await _http
                    .SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct)
                    .ConfigureAwait(false);

                resp.EnsureSuccessStatusCode();

                using var stream = await resp.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
                using var reader = new System.IO.StreamReader(stream, Encoding.UTF8);

                while (!ct.IsCancellationRequested)
                {
                    var line = await reader.ReadLineAsync(ct).ConfigureAwait(false);
                    if (line == null) break; // server closed

                    if (!line.StartsWith("data:", StringComparison.Ordinal)) continue;

                    var json = line["data:".Length..].Trim();
                    if (string.IsNullOrEmpty(json)) continue;

                    TryParseCentrifugo(json);
                }
            }
            catch (OperationCanceledException) { return; }
            catch (Exception ex)
            {
                Log($"SSE error, retry in 5s: {ex.Message}");
                await Task.Delay(5_000, ct).ConfigureAwait(false);
            }
        }
    }

    private void TryParseCentrifugo(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;

            JsonElement songEl;

            // Initial connection message
            if (root.TryGetProperty("connect", out var connect) &&
                connect.TryGetProperty("subs", out var subs))
            {
                foreach (var sub in subs.EnumerateObject())
                {
                    if (TryGetFirstPublication(sub.Value, out songEl))
                    {
                        EmitSong(songEl);
                        return;
                    }
                }
            }

            // Regular push: {"channel":"...","pub":{"data":{...}}}
            if (root.TryGetProperty("pub", out var pub) &&
                pub.TryGetProperty("data", out var data) &&
                TryGetNowPlayingSong(data, out songEl))
            {
                EmitSong(songEl);
            }
        }
        catch (Exception ex)
        {
            Log($"JSON parse error: {ex.Message}");
        }
    }

    private static bool TryGetFirstPublication(JsonElement subEl, out JsonElement song)
    {
        song = default;
        if (!subEl.TryGetProperty("publications", out var pubs)) return false;
        foreach (var pub in pubs.EnumerateArray())
        {
            if (pub.TryGetProperty("data", out var data) &&
                TryGetNowPlayingSong(data, out song))
                return true;
        }
        return false;
    }

    private static bool TryGetNowPlayingSong(JsonElement data, out JsonElement song)
    {
        song = default;
        return data.TryGetProperty("np", out var np) &&
               np.TryGetProperty("now_playing", out var np2) &&
               np2.TryGetProperty("song", out song);
    }

    private void EmitSong(JsonElement song)
    {
        var title  = song.TryGetProperty("title",  out var t) ? t.GetString() ?? "" : "";
        var artist = song.TryGetProperty("artist", out var a) ? a.GetString() ?? "" : "";
        var art    = song.TryGetProperty("art",    out var r) ? r.GetString()      : null;

        Log($"SSE track: {artist} – {title}");
        TrackChanged?.Invoke(title, artist, art);
    }

    // -----------------------------------------------------------------------
    // Generic AzuraCast REST polling  (/api/nowplaying/<station>)
    // -----------------------------------------------------------------------

    private async Task RunRestPollingAsync(string url, CancellationToken ct)
    {
        Log($"REST polling: {url}");
        string lastTitle = "";

        while (!ct.IsCancellationRequested)
        {
            try
            {
                using var resp = await _http.GetAsync(url, ct).ConfigureAwait(false);
                resp.EnsureSuccessStatusCode();

                var body = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
                using var doc = JsonDocument.Parse(body);
                var root = doc.RootElement;

                // AzuraCast /api/nowplaying/<station> shape:
                //   { "now_playing": { "song": { "title", "artist", "art" } } }
                if (root.TryGetProperty("now_playing", out var np) &&
                    np.TryGetProperty("song", out var song))
                {
                    var title  = song.TryGetProperty("title",  out var t) ? t.GetString() ?? "" : "";
                    var artist = song.TryGetProperty("artist", out var a) ? a.GetString() ?? "" : "";
                    var art    = song.TryGetProperty("art",    out var r) ? r.GetString()      : null;

                    if (title != lastTitle)
                    {
                        lastTitle = title;
                        Log($"REST track: {artist} – {title}");
                        TrackChanged?.Invoke(title, artist, art);
                    }
                }
            }
            catch (OperationCanceledException) { return; }
            catch (Exception ex)
            {
                Log($"REST poll error: {ex.Message}");
            }

            await Task.Delay(10_000, ct).ConfigureAwait(false);
        }
    }
}
