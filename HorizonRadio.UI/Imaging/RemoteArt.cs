using System;
using System.Collections.Concurrent;
using System.IO;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Media.Imaging;

namespace HorizonRadio.UI.Imaging;

/// <summary>
/// Loads remote artwork URLs into Avalonia <see cref="Bitmap"/>s for search-result
/// rows. Search sources hand back image URLs (not bytes) to keep result lists cheap
/// to build; this fetches and decodes them lazily, off the UI thread, and caches the
/// downloaded bytes so the same thumbnail (very common — an artist's tracks share
/// album art) is fetched once. Failure-silent: a broken URL just yields the
/// placeholder tile.
/// </summary>
public static class RemoteArt
{
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(10) };

    // Cache the bytes rather than the Bitmap: a Bitmap is a disposable GPU-backed
    // resource we don't want shared across bindings, but re-decoding cached bytes is
    // cheap and keeps each row owning its own image.
    private static readonly ConcurrentDictionary<string, byte[]> Cache = new();

    public static async Task<Bitmap?> LoadAsync(string? url, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(url)) return null;
        try
        {
            if (!Cache.TryGetValue(url, out var bytes))
            {
                bytes = await Http.GetByteArrayAsync(url, ct).ConfigureAwait(false);
                Cache[url] = bytes;
            }
            if (bytes.Length == 0) return null;
            using var ms = new MemoryStream(bytes);
            return new Bitmap(ms);
        }
        catch
        {
            return null; // network/decoding failure → caller shows the placeholder
        }
    }
}
