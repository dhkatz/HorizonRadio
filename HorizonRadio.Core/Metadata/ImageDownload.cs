using System;
using System.Diagnostics;
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

    /// <summary>Download the bytes at <paramref name="url"/>, or null on any failure.</summary>
    public static async Task<byte[]?> TryGetAsync(HttpClient http, string url, CancellationToken ct)
    {
        try { return await http.GetByteArrayAsync(url, ct).ConfigureAwait(false); }
        catch (Exception ex) { Debug.WriteLine($"[hzn-art] {url}: {ex.Message}"); return null; }
    }
}
