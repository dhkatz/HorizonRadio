using System;
using System.Diagnostics;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;

namespace HorizonRadio.Core.Metadata;

/// <summary>
/// Fetches cover-art / thumbnail bytes for the metadata pipeline. One place so the
/// (best-effort, never-throw) failure handling doesn't drift across the providers, and so
/// callers without their own client (the radio station-logo fetch) reuse a shared client
/// instead of spinning up — and leaking — an <see cref="HttpClient"/> per call.
/// </summary>
public static class ImageDownload
{
    public static HttpClient Shared { get; } = new() { Timeout = TimeSpan.FromSeconds(15) };

    /// <summary>Download the bytes at <paramref name="url"/>, or null on any failure. "No such
    /// image" (404/410) is an answer; any other failure is reported to the current
    /// <see cref="CatalogLookup"/>, so a song whose art couldn't be fetched isn't cached art-less.</summary>
    public static async Task<byte[]?> TryGetAsync(HttpClient http, string url, CancellationToken ct)
    {
        try { return await http.GetByteArrayAsync(url, ct).ConfigureAwait(false); }
        catch (HttpRequestException ex) when (ex.StatusCode is HttpStatusCode.NotFound or HttpStatusCode.Gone)
        {
            return null;
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[hzn-art] {url}: {ex.Message}");
            if (!ct.IsCancellationRequested)
            {
                var what = ex is HttpRequestException { StatusCode: { } code } ? $"HTTP {(int)code}" : ex.GetType().Name;
                CatalogLookup.ReportFailure($"art {what}");
            }
            return null;
        }
    }
}
